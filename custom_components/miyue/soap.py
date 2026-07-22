"""Minimal async SOAP client for MiYue private (urn:miyue-hk) services.

async_upnp_client handles the standard MediaRenderer services well, but calling
the private MiyueGroup/MiyueQueue/etc. actions through its generic UpnpAction API
is awkward because those SCPDs use bare A_ARG_Str placeholders for every arg.
A tiny hand-rolled SOAP caller is simpler and matches exactly what the Flutter
controller and the firmware speak on the wire (verified against live devices).

The envelope/headers here are byte-for-byte what the firmware accepts:
    Content-Type: text/xml; charset="utf-8"
    SOAPACTION: "<serviceType>#<action>"
Responses are <u:ActionResponse> with one child element per out-arg.
"""

from __future__ import annotations

import asyncio
import logging
import re
from html import escape
from xml.etree import ElementTree as ET

import aiohttp

_LOGGER = logging.getLogger(__name__)

DEFAULT_TIMEOUT = 6.0

_ENVELOPE = (
    '<?xml version="1.0" encoding="utf-8"?>'
    '<s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/"'
    ' s:encodingStyle="http://schemas.xmlsoap.org/soap/encoding/">'
    "<s:Body>{body}</s:Body></s:Envelope>"
)


class SoapError(Exception):
    """A SOAP call failed (transport error or UPnPError fault)."""

    def __init__(self, message: str, *, fault_code: str | None = None):
        super().__init__(message)
        self.fault_code = fault_code


def _local(tag: str) -> str:
    """Strip an XML namespace from a tag name."""
    return tag.rsplit("}", 1)[-1] if "}" in tag else tag


# Chars illegal in XML 1.0 (DLNA servers leak them from raw file tags), and
# bare ampersands that aren't part of an entity (WMP-style servers emit them).
_XML_ILLEGAL = re.compile("[\x00-\x08\x0b\x0c\x0e-\x1f]")
_BARE_AMP = re.compile(r"&(?!#\d+;|#x[0-9a-fA-F]+;|[A-Za-z][A-Za-z0-9]*;)")


def parse_xml_lenient(text: str) -> ET.Element:
    """Parse XML strictly, then retry after scrubbing illegal tokens.

    Third-party DMS boxes (QNAP/WMP/minidlna) embed control chars or bare '&'
    from raw file metadata; one bad byte must not kill a whole browse page.
    Raises ET.ParseError only if the scrubbed document still fails.
    """
    try:
        return ET.fromstring(text)
    except ET.ParseError:
        cleaned = _BARE_AMP.sub("&amp;", _XML_ILLEGAL.sub("", text))
        return ET.fromstring(cleaned)


def decode_mixed(raw: bytes) -> str:
    """Decode a firmware response that may mix UTF-8 with raw-GBK byte runs.

    The Linux firmware can emit local-file ID3 tags as their original GBK
    bytes verbatim inside an otherwise UTF-8 XML document, which makes a
    plain .decode("utf-8") blow up (e.g. 0xCF mid-document). Decode the
    valid UTF-8 stretches normally and transcode each invalid run as GBK,
    falling back to latin1 so this never raises.
    """
    try:
        return raw.decode("utf-8")
    except UnicodeDecodeError:
        pass
    out: list[str] = []
    i = 0
    n = len(raw)
    while i < n:
        try:
            out.append(raw[i:].decode("utf-8"))
            break
        except UnicodeDecodeError as err:
            if err.start > 0:
                out.append(raw[i:i + err.start].decode("utf-8"))
            j = i + err.start
            run = bytearray()
            # Consume GBK two-byte pairs: lead 0x81-0xFE + trail 0x40-0xFE.
            while j < n and raw[j] >= 0x81:
                run.append(raw[j])
                j += 1
                if j < n and 0x40 <= raw[j] <= 0xFE and raw[j] != 0x7F:
                    run.append(raw[j])
                    j += 1
            if not run:  # invalid byte below 0x81 -- skip it defensively
                j += 1
            try:
                out.append(run.decode("gbk"))
            except UnicodeDecodeError:
                out.append(run.decode("latin1"))
            i = j
    return "".join(out)


class SoapClient:
    """Fire SOAP actions at one device's control URLs over a shared session."""

    def __init__(
        self,
        session: aiohttp.ClientSession,
        base_url: str,
        *,
        timeout: float = DEFAULT_TIMEOUT,
    ) -> None:
        # base_url like "http://192.168.1.213:49498" (no trailing slash).
        self._session = session
        self._base = base_url.rstrip("/")
        self._timeout = aiohttp.ClientTimeout(total=timeout)

    @property
    def base_url(self) -> str:
        return self._base

    def with_base(self, base_url: str) -> "SoapClient":
        """Return a client pointed at a new base (used on port drift)."""
        return SoapClient(self._session, base_url, timeout=self._timeout.total)

    async def call(
        self,
        control_path: str,
        service_type: str,
        action: str,
        args: dict[str, object] | None = None,
    ) -> dict[str, str]:
        """Invoke `action` on `service_type` at `control_path`.

        Returns the response out-args as a plain {name: text} dict. Text is
        already XML-unescaped by the parser, so DIDL payloads come back as
        real XML strings ready to parse again.
        """
        args = args or {}
        inner = "".join(
            f"<{name}>{escape(str(value))}</{name}>" for name, value in args.items()
        )
        body = f'<u:{action} xmlns:u="{service_type}">{inner}</u:{action}>'
        payload = _ENVELOPE.format(body=body).encode("utf-8")
        url = f"{self._base}{control_path}"
        headers = {
            "Content-Type": 'text/xml; charset="utf-8"',
            "SOAPACTION": f'"{service_type}#{action}"',
            # pupnp's miniserver drops large POSTs that ride a reused
            # keep-alive connection it has already half-closed ("Server
            # disconnected"/"Broken pipe" on ReplaceQueue was exactly this).
            # Force one fresh connection per request — LAN cost is nil.
            "Connection": "close",
        }

        last_err: Exception | None = None
        for attempt in range(2):
            try:
                async with self._session.post(
                    url, data=payload, headers=headers, timeout=self._timeout
                ) as resp:
                    # Never resp.text(): firmware may embed raw-GBK ID3 bytes
                    # in the UTF-8 XML (kills strict decoding).
                    text = decode_mixed(await resp.read())
                    if resp.status != 200:
                        fault = _parse_fault(text)
                        raise SoapError(
                            f"{action} on {url} -> HTTP {resp.status}"
                            + (f" ({fault})" if fault else ""),
                            fault_code=fault,
                        )
                return _parse_response(text, action)
            except (aiohttp.ServerDisconnectedError, aiohttp.ClientOSError) as err:
                # Connection died mid-request (the pupnp keep-alive race) --
                # the action was not processed; one retry on a fresh socket.
                last_err = err
                if attempt == 0:
                    await asyncio.sleep(0.2)
                    continue
            except aiohttp.ClientError as err:
                raise SoapError(f"{action} on {url} transport error: {err}") from err
            except asyncio.TimeoutError as err:
                raise SoapError(f"{action} on {url} timed out") from err
        raise SoapError(
            f"{action} on {url} connection dropped twice: {last_err}"
        ) from last_err


def _parse_response(text: str, action: str) -> dict[str, str]:
    """Extract out-args from a <u:ActionResponse> body."""
    try:
        root = parse_xml_lenient(text)
    except ET.ParseError as err:
        raise SoapError(f"{action}: malformed response XML: {err}") from err

    # Find the <u:...Response> element regardless of namespace prefixes.
    resp_el = None
    for el in root.iter():
        if _local(el.tag) == f"{action}Response":
            resp_el = el
            break
    if resp_el is None:
        fault = _parse_fault(text)
        if fault:
            raise SoapError(f"{action}: UPnPError {fault}", fault_code=fault)
        raise SoapError(f"{action}: no {action}Response in reply")

    return {_local(child.tag): (child.text or "") for child in resp_el}


def _parse_fault(text: str) -> str | None:
    """Return the UPnP errorCode from a SOAP fault, if present."""
    try:
        root = ET.fromstring(text)
    except ET.ParseError:
        return None
    for el in root.iter():
        if _local(el.tag) == "errorCode":
            return el.text
    return None
