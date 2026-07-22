"""Multi-room grouping orchestration (mirror model).

Grouping state lives on each device; there is no whole-group query. The
controller reads every device's GetGroupInfo, clusters by GroupID, and drives
joins/leaves with the exact sequence the Flutter controller uses (verified in
docs/CONTRACT.md):

  JOIN  : read master's authoritative gid/multicast/masterIp -> SetGroupConfig
          the master FIRST (Role=Master) -> then each slave (Role=Slave) with
          the master's values -> settle ~900ms -> re-poll to confirm.
  UNJOIN: LeaveGroup() on the leaving device; if that would leave <2 members,
          dissolve the whole group by LeaveGroup() on EVERY member (never rely
          on the device to cascade -- Linux firmware does not).

Invariants: masterIp must equal the master's current IPv4 (self-derived; never
push a foreign value); never fabricate gid/multicast (read from the master).
"""

from __future__ import annotations

import asyncio
import ipaddress
import logging

from .const import GROUP_APPLY_SETTLE, GROUP_RETRY_DELAY, ROLE_MASTER, ROLE_SLAVE
from .device import GroupInfo, MiyueDevice
from .soap import SoapError

_LOGGER = logging.getLogger(__name__)


class GroupError(Exception):
    """A join/unjoin could not be applied."""


def is_ipv4(value: str) -> bool:
    """Real routable-ish IPv4 only: reject empty/IPv6/hostname/0/broadcast."""
    if not value:
        return False
    try:
        addr = ipaddress.ip_address(value)
    except ValueError:
        return False
    if addr.version != 4:
        return False
    if addr.is_unspecified or addr.is_loopback:  # 0.0.0.0 / 127.x
        return False
    if value == "255.255.255.255":
        return False
    return True


def cluster_members(
    registry: dict, self_udn: str
) -> tuple[list[str], str | None]:
    """Return (member_udns, leader_udn) for the group self_udn belongs to.

    Mirror-model clustering: group by GroupID over all devices whose latest
    GroupInfo says they are non-standalone. A cluster of >=2 is a real group
    (leader = the Master); a lone member (size 1) is presented as standalone,
    so this returns ([], None) for it. `Role == None` vetoes any gid.
    """
    me = _group_info(registry, self_udn)
    if me is None or me.is_standalone or not me.group_id or me.group_id == "null":
        return [], None

    members: list[str] = []
    leader: str | None = None
    for udn in registry:
        info = _group_info(registry, udn)
        if info is None or info.is_standalone:
            continue
        if info.effective_group_id and info.effective_group_id == me.effective_group_id:
            members.append(udn)
            if info.is_master:
                leader = udn

    if len(members) < 2:
        return [], None
    # Leader first.
    if leader and leader in members:
        members.remove(leader)
        members.insert(0, leader)
    return members, leader


def _group_info(registry: dict, udn: str) -> GroupInfo | None:
    runtime = registry.get(udn)
    if runtime is None or runtime.coordinator.data is None:
        return None
    return runtime.coordinator.data.group


async def async_join(
    registry: dict, master_udn: str, slave_udns: list[str]
) -> None:
    """Form/extend a group with master_udn as leader and slave_udns as members."""
    master_rt = registry.get(master_udn)
    if master_rt is None:
        raise GroupError(f"unknown master {master_udn}")
    master = master_rt.device

    # Step 1: authoritative transport from the master (self-derived).
    info = await master.get_group_info()
    gid = info.group_id
    multicast = info.multicast_addr
    master_ip = info.master_ip if is_ipv4(info.master_ip) else master.host
    if not gid or gid == "null" or not multicast:
        raise GroupError(f"master {master_udn} has no valid group identity")
    if not is_ipv4(master_ip):
        raise GroupError(f"master {master_udn} has no valid IPv4 ({master_ip!r})")

    # Step 2: configure the master FIRST, then the slaves.
    await _set_config_retry(master, master.udn, gid, master_ip, multicast, ROLE_MASTER)
    for slave_udn in slave_udns:
        slave_rt = registry.get(slave_udn)
        if slave_rt is None:
            _LOGGER.warning("MiYue join: unknown slave %s, skipping", slave_udn)
            continue
        if slave_rt.device.host == master_ip:
            _LOGGER.warning("MiYue join: slave %s == master IP, skipping", slave_udn)
            continue
        await _set_config_retry(
            slave_rt.device, slave_rt.device.udn, gid, master_ip, multicast, ROLE_SLAVE
        )

    # Step 3: settle, then re-poll to confirm (SOAP success is not proof).
    await asyncio.sleep(GROUP_APPLY_SETTLE)
    await _refresh_all(registry, [master_udn, *slave_udns])


async def async_unjoin(registry: dict, udn: str) -> None:
    """Remove `udn` from its group; dissolve the group if it would drop <2."""
    members, _leader = cluster_members(registry, udn)
    to_leave: list[str]
    if len(members) <= 2:
        # A 2-member group can't lose one and stay a group -> dissolve fully.
        to_leave = members or [udn]
    else:
        to_leave = [udn]

    for member_udn in to_leave:
        member_rt = registry.get(member_udn)
        if member_rt is None:
            continue
        try:
            await member_rt.device.leave_group()
        except SoapError as err:
            _LOGGER.warning("MiYue unjoin: LeaveGroup on %s failed: %s", member_udn, err)

    await asyncio.sleep(GROUP_APPLY_SETTLE)
    await _refresh_all(registry, to_leave)


async def _set_config_retry(
    device: MiyueDevice,
    target_uuid: str,
    gid: str,
    master_ip: str,
    multicast: str,
    role: str,
) -> None:
    """SetGroupConfig with one retry after a short delay (reference behavior)."""
    try:
        await device.set_group_config(target_uuid, gid, master_ip, multicast, role)
        return
    except SoapError as err:
        _LOGGER.debug("MiYue SetGroupConfig(%s) retrying: %s", role, err)
    await asyncio.sleep(GROUP_RETRY_DELAY)
    await device.set_group_config(target_uuid, gid, master_ip, multicast, role)


async def _refresh_all(registry: dict, udns: list[str]) -> None:
    tasks = []
    for udn in set(udns):
        runtime = registry.get(udn)
        if runtime is not None:
            tasks.append(runtime.coordinator.async_request_refresh())
    if tasks:
        await asyncio.gather(*tasks, return_exceptions=True)
