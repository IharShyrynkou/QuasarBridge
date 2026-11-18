# MITM-сертификат QuasarBridge (~100 лет, своя CA)

Локальная сеть: DNS уводит `uniproxy.alice.yandex.net` на ПК с QuasarBridge.  
Станция **доверяет только если** в `/system/etc/security/cacerts/` лежит **ваш корневой CA** (файл `*.0`).

Публичный «вечный» cert на домены Яндекса не выдают — используем **свой Root CA**.

---

## Артефакты после генерации

| Файл | Назначение |
|------|------------|
| `certs/root-ca.pem` | Корень (100 лет) |
| `certs/root-ca.key` | Ключ корня (**секрет**, бэкап) |
| `certs/quasar-mitm.pem` | Leaf (сервер) |
| `certs/quasar-all.key` | Ключ leaf |
| `certs/fullchain.pem` | leaf + root → **QuasarBridge `TlsCertPath`** |
| `certs/quasar-mitm.pfx` | Импорт в Windows `CurrentUser\My` |
| `certs/<hash>.0` | Корень в формате Android → **станция** |
| `certs/mitm-cert-meta.json` | Thumbprint, даты, пути |
| `certs/san-mitm.cnf` | Список SAN (править перед перевыпуском) |

---

## Быстрый путь (Windows)

```powershell
cd "C:\Cursor AI\Projects\QuasarBridge"

# 1) Только генерация (~36500 дней)
.\scripts\Generate-QuasarMitmCert.ps1

# 2) Генерация + Windows + станция + appsettings (нужен Админ PowerShell)
.\scripts\Install-QuasarMitmCert.ps1 -Regenerate
```

Параметры установщика:

```powershell
.\scripts\Install-QuasarMitmCert.ps1 `
  -StationIp 192.168.0.195 `
  -SshKey C:\Users\Admin\Documents\ssh\id_rsa `
  -SkipStation   # только ПК, без SSH на станцию
```

После установки **перезапустите QuasarBridge**.

---

## Ручная генерация (OpenSSL)

Требуется `openssl` (Git for Windows: `C:\Program Files\Git\usr\bin\openssl.exe`).

```powershell
$ssl = "C:\Program Files\Git\usr\bin\openssl.exe"
$cd  = "C:\Cursor AI\Projects\QuasarBridge\certs"
$D   = 36500

& $ssl genrsa -out "$cd\root-ca.key" 4096
& $ssl req -x509 -new -nodes -key "$cd\root-ca.key" -sha256 -days $D `
  -out "$cd\root-ca.pem" -subj "/CN=QuasarBridge Homelab Root CA/O=QuasarBridge Homelab/C=RU"

& $ssl genrsa -out "$cd\quasar-all.key" 2048
& $ssl req -new -key "$cd\quasar-all.key" -out "$cd\leaf.csr" -config "$cd\san-mitm.cnf"
& $ssl x509 -req -in "$cd\leaf.csr" -CA "$cd\root-ca.pem" -CAkey "$cd\root-ca.key" -CAcreateserial `
  -out "$cd\quasar-mitm.pem" -days $D -sha256 -extfile "$cd\san-mitm.cnf" -extensions req_ext

Get-Content "$cd\quasar-mitm.pem","$cd\root-ca.pem" | Set-Content "$cd\fullchain.pem" -Encoding ascii

$h = & $ssl x509 -inform PEM -subject_hash_old -in "$cd\root-ca.pem" | Select-Object -First 1
Copy-Item "$cd\root-ca.pem" "$cd\$h.0"
```

---

## Куда ставится

### ПК (QuasarBridge)

1. **Файлы:** `QuasarBridge:TlsCertPath` = `...\certs\fullchain.pem`, `TlsKeyPath` = `...\certs\quasar-all.key`.
2. **Schannel:** импорт `quasar-mitm.pfx` в `Cert:\CurrentUser\My`, thumbprint в `TlsStoreThumbprint`.
3. Скрипт `Install-QuasarMitmCert.ps1` делает п.1–2 и правит `appsettings.json`.

### Станция Макс (root + SSH)

1. Копия корня в **`/data/quasar-mitm/cacerts/<hash>.0`** (переживает ребут).
2. Копия в **`/system/etc/security/cacerts/<hash>.0`** (TLS сразу).
3. Блок **`quasar-mitm-cacert`** в `/data/local/tmp/myscript-max.sh` — снова кладёт CA после boot (verity откатывает `/system`).

Проверка на станции:

```bash
ls -l /system/etc/security/cacerts/*.0 | tail -3
ls -l /data/quasar-mitm/cacerts/
```

---

## Проверка end-to-end

### DNS

С станции IP должен быть **ПК с бриджем**, не `213.180.*`:

```bash
ping -c1 uniproxy.alice.yandex.net
```

### TLS с ПК (подставьте IP бриджа)

```powershell
$MITM = "192.168.0.176"
curl.exe -sk --resolve uniproxy.alice.yandex.net:443:$MITM https://uniproxy.alice.yandex.net/healthz
```

### QuasarBridge

В логе:

```text
station connected from 192.168.0.195 sni=uniproxy.alice.yandex.net
upstream connected wss://uniproxy.alice.yandex.net/uni.ws
```

---

## Срок «100 лет»

- В скрипте по умолчанию **`36500` дней** (~100 лет).
- Юридически/криптографически разумнее иногда **перевыпускать leaf** (тот же root).
- Смена root на станции = новый `<hash>.0` + удаление старого `.0`, если hash другой.

---

## Добавить SAN

Отредактируйте `certs/san-mitm.cnf` → снова:

```powershell
.\scripts\Install-QuasarMitmCert.ps1 -Regenerate
```

Станцию передёргивать только если менялся **root**; при смене только leaf достаточно перезапуска QuasarBridge.

---

## Откат (если Алиса «плохо себя чувствует»)

1. `appsettings.json` → снова `Pictures\certs\fullchain.pem` + старый `TlsStoreThumbprint` (`1B607464…`).
2. Перезапуск QuasarBridge.
3. На станции: `rm /system/etc/security/cacerts/4f8d26e4.0`, `rm -rf /data/quasar-mitm`, убрать блок `quasar-mitm-cacert` из `myscript-max.sh`.
4. По желанию: `killall com.yandex.io.sdk` или перезагрузка станции.

```powershell
# Быстро: пути как до эксперимента
# TlsCertPath: C:\Users\Admin\Pictures\certs\fullchain.pem
# TlsStoreThumbprint: 1B607464F66256BD73B6A9BB6B7FFCD33A345C68
```

---

## Безопасность

- **`root-ca.key` и `quasar-all.key` не коммитить** (при необходимости добавьте в `.gitignore`).
- CA действует только в вашей LAN при вашем DNS.
- Поведение прошивки Яндекса (pinning) со временем может измениться — это не лечится сроком cert.