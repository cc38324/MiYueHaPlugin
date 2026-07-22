"""Pure-logic tests for grouping — clustering + IPv4 validation."""

from dataclasses import dataclass
from types import SimpleNamespace

from custom_components.miyue.device import GroupInfo
from custom_components.miyue.group import cluster_members, is_ipv4


def test_is_ipv4():
    assert is_ipv4("192.168.1.21")
    assert not is_ipv4("")
    assert not is_ipv4("0.0.0.0")
    assert not is_ipv4("255.255.255.255")
    assert not is_ipv4("127.0.0.1")
    assert not is_ipv4("fe80::1")
    assert not is_ipv4("speaker.local")


def _rt(group: GroupInfo, entity_id: str):
    """Fake MiyueRuntimeData: coordinator.data.group + entity_id."""
    data = SimpleNamespace(group=group)
    coord = SimpleNamespace(data=data)
    return SimpleNamespace(coordinator=coord, entity_id=entity_id)


def test_cluster_two_member_group():
    gid = "aaaa1111"
    reg = {
        "uuid:A": _rt(GroupInfo(role="Master", group_id=gid, master_ip="192.168.1.21"),
                      "media_player.a"),
        "uuid:B": _rt(GroupInfo(role="Slave", group_id=gid, master_ip="192.168.1.21"),
                      "media_player.b"),
    }
    members, leader = cluster_members(reg, "uuid:B")
    assert members == ["uuid:A", "uuid:B"]  # leader (Master) first
    assert leader == "uuid:A"


def test_standalone_role_none_vetoes_gid():
    # Device still carries a stale gid but reports Role=None -> standalone.
    reg = {
        "uuid:A": _rt(GroupInfo(role="None", group_id="stale", master_ip=""),
                      "media_player.a"),
    }
    members, leader = cluster_members(reg, "uuid:A")
    assert members == []
    assert leader is None


def test_lone_master_is_standalone():
    reg = {
        "uuid:A": _rt(GroupInfo(role="Master", group_id="g", master_ip="192.168.1.21"),
                      "media_player.a"),
    }
    members, _ = cluster_members(reg, "uuid:A")
    assert members == []  # cluster of 1 -> presented as standalone
