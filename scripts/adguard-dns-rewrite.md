# AdGuard Home — DNS rewrite для QuasarBridge

Цель: станция резолвит `uniproxy.alice.yandex.net` в IP этого ПК (`192.168.0.176`),
а QuasarBridge на `:443` принимает WSS с сертификатом, у которого этот хост в SAN.

## Rewrite

В AdGuard Home → **Filters → DNS rewrites → Add DNS rewrite**:

| Domain | Answer |
|---|---|
| `uniproxy.alice.yandex.net` | `192.168.0.176` |

Опционально (если будете MITM и другие домены из того же `fullchain.pem`):

| Domain | Answer |
|---|---|
| `rpc.alice.yandex.net` | `192.168.0.176` |
| `quasar.yandex.net` | `192.168.0.176` |

**Не** реврайтет на ПК:

- `iot.quasar.yandex.ru` / `api.quasar.yandex.ru` — умный дом
- `voiceservices.yandex.net`, `ws.api.music.yandex.net`, `strm.*` — голос/музыка (пока не готовы отдельные хендлеры)

## Проверка со станции

```bash
# через SSH
getprop net.dns1
# или
toybox nslookup uniproxy.alice.yandex.net
```

Ожидаемо: `192.168.0.176`.

## Проверка с ПК

```powershell
# TLS handshake как будто SNI = uniproxy
curl.exe -v --resolve uniproxy.alice.yandex.net:443:192.168.0.176 https://uniproxy.alice.yandex.net/healthz
```

## Важно

QuasarBridge сам ходит в настоящий Yandex через **DNS-over-HTTPS** (Cloudflare/Google),
поэтому rewrite в AdGuard его не зацикливает.

Станция должна использовать AdGuard как DNS (обычно через DHCP OpenWrt → AdGuard).

## Firewall на ПК

Разрешите входящий TCP 443 на интерфейс `192.168.0.176` (админ PowerShell):

```powershell
New-NetFirewallRule -DisplayName 'QuasarBridge HTTPS 443' -Direction Inbound -Action Allow -Protocol TCP -LocalPort 443 -Profile Private,Domain
```
