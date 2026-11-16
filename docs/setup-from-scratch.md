# QuasarBridge — настройка с нуля

Предполагается, что на Станции Макс **уже есть root и SSH** (вход `root@<IP>` по ключу).  
Как получить root/SSH — не в этом документе.

Цель: станция ходит в `uniproxy.alice.yandex.net` на этот ПК, QuasarBridge проксирует в Яндекс и умеет локально перехватывать команды (пример: «Включи сериал» → PotPlayer).

---

## Роли в сети (пример)

| Узел | IP | Роль |
|---|---|---|
| ПК с QuasarBridge | `192.168.0.176` | MITM TLS `:443` / `:8443` |
| Станция Макс | `192.168.0.195` | клиент uniproxy |
| AdGuard Home | `192.168.0.200:30004` | DNS + rewrite |
| OpenWrt | `192.168.0.1` | опциональный dnsmasq pin / firewall |

Подставьте свои IP, если отличаются.

SSH-ключ станции (пример): `C:\Users\Admin\Documents\ssh\id_rsa`

```powershell
$STATION = "192.168.0.195"
$MITM    = "192.168.0.176"
$KEY     = "C:\Users\Admin\Documents\ssh\id_rsa"
ssh -i $KEY -o IdentitiesOnly=yes root@$STATION "id"
```

---

## 1. ПК: сертификат в хранилище Windows

Schannel не любит ephemeral key. Сертификат из `certs/` один раз кладём в `CurrentUser\My`.

```powershell
cd C:\Users\Admin\Documents\Projects\QuasarBridge

# PFX уже есть в certs\, либо собрать из pem+key:
# openssl pkcs12 -export -out certs\quasar-mitm.pfx -inkey certs\quasar-all.key -in certs\fullchain.pem

$pfx = Resolve-Path .\certs\quasar-mitm.pfx
# если пароля нет — пустая строка; иначе подставьте
Import-PfxCertificate -FilePath $pfx -CertStoreLocation Cert:\CurrentUser\My -Exportable

Get-ChildItem Cert:\CurrentUser\My |
  Where-Object { $_.Subject -match 'quasar|yandex' -or $_.DnsNameList -match 'uniproxy' } |
  Format-List Thumbprint, Subject, NotAfter
```

Thumbprint пропишите в `appsettings.json` → `QuasarBridge:TlsStoreThumbprint`  
(сейчас там значение для текущего набора сертификатов).

Убедитесь, что в SAN есть `uniproxy.alice.yandex.net`.

---

## 2. ПК: файрвол

Сеть часто в профиле **Public** — без правила вход на `:443` будет timeout.

```powershell
sudo netsh advfirewall firewall add rule name="QuasarBridge MITM 443" dir=in action=allow protocol=TCP localport=443 profile=any
sudo netsh advfirewall firewall add rule name="QuasarBridge MITM 8443" dir=in action=allow protocol=TCP localport=8443 profile=any
```

Проверка с самой станции (после DNS, §4):

```powershell
ssh -i $KEY -o IdentitiesOnly=yes root@$STATION "toybox timeout 3 toybox nc $MITM 443 </dev/null; echo nc_exit=\$?"
```

`nc_exit=0` — порт открыт.

---

## 3. AdGuard Home: DNS rewrite

Админка (пример): `http://192.168.0.200:30004`

Добавьте / проверьте rewrite:

| Domain | Answer |
|---|---|
| `uniproxy.alice.yandex.net` | `192.168.0.176` |
| `uniproxy-quic.alice.yandex.net` | `192.168.0.176` |

Без второго пункта станция часто уходит на QUIC в обход MITM.

API (Basic Auth):

```powershell
$ag = "http://192.168.0.200:30004"
$pair = [Convert]::ToBase64String([Text.Encoding]::ASCII.GetBytes("USER:PASS"))
$h = @{ Authorization = "Basic $pair"; "Content-Type" = "application/json" }

# список
Invoke-RestMethod "$ag/control/rewrite/list" -Headers @{ Authorization = $h.Authorization }

# добавить (если нет)
$body = @{ domain = "uniproxy.alice.yandex.net"; answer = $MITM } | ConvertTo-Json
Invoke-RestMethod -Method Post -Uri "$ag/control/rewrite/add" -Headers $h -Body $body
$body = @{ domain = "uniproxy-quic.alice.yandex.net"; answer = $MITM } | ConvertTo-Json
Invoke-RestMethod -Method Post -Uri "$ag/control/rewrite/add" -Headers $h -Body $body
```

---

## 4. OpenWrt (рекомендуется): pin в dnsmasq

На роутере (`root@192.168.0.1`):

```sh
mkdir -p /etc/dnsmasq.d
cat >/etc/dnsmasq.d/uniproxy-mitm.conf <<EOF
address=/uniproxy.alice.yandex.net/192.168.0.176
address=/uniproxy-quic.alice.yandex.net/192.168.0.176
EOF
/etc/init.d/dnsmasq restart
nslookup uniproxy.alice.yandex.net 127.0.0.1
```

Опционально (если станция всё же бьёт в реальные IP Яндекса): DNAT/reject QUIC — см. рабочий пример в истории настройки; для старта часто хватает DNS + AdGuard.

---

## 5. Станция: DNS на AdGuard

```powershell
ssh -i $KEY -o IdentitiesOnly=yes root@$STATION "getprop net.dns1; getprop net.dns2"
```

Нужно `192.168.0.200` (или ваш AdGuard). Если другой DNS — поправьте Wi‑Fi/DHCP на роутере, чтобы станция получала AdGuard как DNS.

Проверка резолва:

```powershell
ssh -i $KEY -o IdentitiesOnly=yes root@$STATION "ping -c1 -W2 uniproxy.alice.yandex.net; ping -c1 -W2 uniproxy-quic.alice.yandex.net"
```

Оба должны отвечать с **`192.168.0.176`**, не с `213.180.*` / `93.158.*`.

---

## 6. Запуск QuasarBridge

```powershell
cd C:\Users\Admin\Documents\Projects\QuasarBridge
dotnet build -c Release
dotnet run -c Release --no-launch-profile --project .\QuasarBridge.csproj
# или: .\scripts\start.ps1
```

В `appsettings.json` для перехвата:

- `Mode`: `hybrid`
- `TlsBindAddress`: `192.168.0.176`
- `TlsPort`: `443`
- `HttpPort`: `8443` (health/swagger на loopback; TLS на LAN `:8443` тоже)
- пути к `certs\fullchain.pem` / `quasar-all.key` и `TlsStoreThumbprint`

Проверка с ПК:

```powershell
Invoke-RestMethod http://127.0.0.1:8443/healthz
Invoke-RestMethod http://127.0.0.1:8443/api/rules
```

Ожидаемо: `status=ok`, `mode=hybrid`, есть правило `open_series_potplayer`.

TLS:

```powershell
curl.exe -sk --resolve uniproxy.alice.yandex.net:443:192.168.0.176 https://uniproxy.alice.yandex.net/healthz
```

---

## 7. Поднять сессию станции через мост

После старта bridge перезапустите SDK на станции (чтобы переподключила WSS):

```powershell
ssh -i $KEY -o IdentitiesOnly=yes root@$STATION @"
kill -9 `$(pidof com.yandex.io.sdk) 2>/dev/null
am force-stop com.yandex.io.sdk
sleep 2
kill -9 `$(pidof com.yandex.tv.alice) 2>/dev/null
sleep 3
pidof com.yandex.io.sdk; pidof com.yandex.tv.alice
"@
```

В логах bridge / `healthz` → `activeConnections >= 1`, растут счётчики сообщений.

---

## 8. Локальный перехват «Включи сериал»

Правило в SQLite (`App_Data/quasar-bridge.db`, создаётся при старте), смотреть/править:

`GET/POST http://127.0.0.1:8443/api/rules`

Смысл: голос уходит в облако **аудио**; текст фразы на мосту не виден.  
Перехват идёт по **ответу Алисы** (уточнение сериала / «подключите к экрану» / `video_play`).  
Дальше: открыть PotPlayer, ответ облака пропустить «пустым», **Ogg/TTS отрезать** (у Ogg часто префикс перед `OggS`).

После смены правил:

```powershell
Invoke-RestMethod -Method Post http://127.0.0.1:8443/api/rules/reload
```

---

## Чеклист проверки

Отмечайте по порядку.

### Доступ

- [ ] `ssh -i ... root@<станция> id` → `uid=0`
- [ ] (если нужен) `adb connect <станция>:5555` и root-shell живы

### DNS

- [ ] У станции `net.dns1` = AdGuard
- [ ] `ping uniproxy.alice.yandex.net` → **`192.168.0.176`**
- [ ] `ping uniproxy-quic.alice.yandex.net` → **`192.168.0.176`**
- [ ] В AdGuard есть rewrite на оба имени
- [ ] (если используете) OpenWrt `address=/uniproxy.../` смотрит на MITM

### ПК / TLS / firewall

- [ ] Сертификат в `Cert:\CurrentUser\My`, thumbprint совпадает с конфигом
- [ ] SAN содержит `uniproxy.alice.yandex.net`
- [ ] Firewall разрешает inbound TCP **443** и **8443** (в т.ч. Public)
- [ ] `Test-NetConnection 192.168.0.176 -Port 443` с ПК = True
- [ ] Со станции `nc`/коннект на `192.168.0.176:443` не висит вечно

### Bridge

- [ ] `http://127.0.0.1:8443/healthz` → `ok`, `mode=hybrid`
- [ ] Слушает `192.168.0.176:443` (и при необходимости `:8443`)
- [ ] Upstream через DoH (в логах есть connect upstream, нет петли на себя)
- [ ] После рестарта SDK: `activeConnections >= 1`

### Перехват

- [ ] `GET /api/rules` → правило серии enabled
- [ ] Голос «Включи сериал» / «Включи мне сериал» → открывается PotPlayer
- [ ] Алиса **не** озвучивает «подключите к экрану» / «нет интернета»
- [ ] В логах есть `local silenced` / `dropped cloud audio` при перехвате
- [ ] Обычные команды Алисы (время и т.п.) по-прежнему отвечают голосом

### Если что-то красное

| Симптом | Куда смотреть |
|---|---|
| ping uniproxy → IP Яндекса | AdGuard rewrite / DNS станции / OpenWrt |
| TCP timeout на :443 со станции | Windows firewall / bind address |
| Handshake fail / EOF | PFX в CurrentUser\My, не EphemeralKeySet |
| Нет WSS на bridge | рестарт `com.yandex.io.sdk`, QUIC rewrite |
| PotPlayer есть, но Алиса говорит | детект Ogg с префиксом, mute после local |
| «Нет интернета» после перехвата | не глушить служебные AAPI целиком — только audio/TTS |

---

## Быстрый «всё ок» в одну минуту

```powershell
$STATION="192.168.0.195"; $KEY="C:\Users\Admin\Documents\ssh\id_rsa"
ssh -i $KEY -o IdentitiesOnly=yes root@$STATION "getprop net.dns1; ping -c1 -W2 uniproxy.alice.yandex.net"
Invoke-RestMethod http://127.0.0.1:8443/healthz | Format-List status,mode,routingRules
Invoke-RestMethod http://127.0.0.1:8443/api/rules | Select-Object -ExpandProperty name
Get-NetTCPConnection -LocalAddress 192.168.0.176 -LocalPort 443 -State Listen
```
