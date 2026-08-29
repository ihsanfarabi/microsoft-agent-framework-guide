# VPN Policy

All remote access to MafCorp internal systems must go through the corporate VPN. Direct SSH or RDP to internal servers from outside the office network is prohibited.

VPN reconnects must use MFA every 8 hours. Sessions that remain connected do not re-prompt, but any reconnect after a drop requires a fresh MFA approval.

The VPN client is MafVPN 4.x and is available from the software portal. Personal VPN clients are not permitted on company machines.

Split tunneling is disabled by default. Exceptions require approval from the Security team and are reviewed quarterly.

Guest and contractor accounts are limited to the VPN gateway named `vpn-guest` and are automatically revoked after 90 days unless renewed by the sponsoring manager.
