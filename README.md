# Phipes.Assistant

Infraestructura para un **asistente personal autónomo** que opera como una persona dentro
de Microsoft 365 (Teams + Mail) y se ayuda de Claude Code corriendo en modo headless
para razonar y responder. Pensado para una casa/oficina pequeña con un Windows Server
on-premise, una conexión a internet con IP dinámica, y un mailbox Microsoft 365 dedicado
para el asistente.

> Licencia MIT. Hecho por Felipe Hernández. Úselo libremente, manteniendo el aviso de
> copyright.

---

## ¿Qué hace este repositorio?

Dos servicios independientes que comparten infraestructura mínima:

1. **`Phipes.Assistant.DdnsWorker`** — Mantiene los registros DNS apex de uno o más
   dominios apuntando a la IP pública dinámica de la casa/oficina. Útil cuando se
   hostean servicios en infraestructura propia detrás de un router/firewall con IP que
   cambia.

2. **`Phipes.Assistant.WebhookHandler`** — Recibe notificaciones de Microsoft Graph
   (Teams chat + Mail) sobre la cuenta del asistente, las descifra, las despacha a
   Claude Code en modo `--print`, y postea las respuestas al canal correspondiente. Es
   lo que convierte un mailbox y una cuenta Teams en "una persona que responde sola".

Cada servicio puede usarse por separado.

---

## Arquitectura general

```
                          ┌────────────────────────┐
                          │  Microsoft 365 (cloud) │
                          │  Teams + Mail + Graph  │
                          └───────────┬────────────┘
                                      │  webhook
                                      ▼
┌──────────────┐   webhook   ┌──────────────────────┐
│  Internet    ├────────────►│  IIS site            │
│  (DDNS via   │             │  Phipes.Assistant.   │
│  Cloudflare) │             │  WebhookHandler      │
└──────┬───────┘             │                      │
       │                     │  • Descifra payload  │
       │                     │  • Valida JWT        │
       │                     │  • Dedupe (MSSQL)    │
       │                     │  • Invoca claude.exe │
       │                     │  • Postea reply      │
       │                     └──────────┬───────────┘
       │                                │
       │                                ▼
       │                     ┌──────────────────────┐
       │                     │  claude.exe --print  │
       │                     │  (Claude Code)       │
       │                     │  + skills personales │
       │                     └──────────────────────┘
       │
       │   (la misma máquina hostea también:)
       │   
       ▼
┌──────────────────────┐
│  Phipes.Assistant.   │
│  DdnsWorker          │
│                      │
│  • Lee IP pública    │
│  • Actualiza apex DNS│
│    en Cloudflare     │
└──────────────────────┘
```

---

## Phipes.Assistant.DdnsWorker

Servicio Windows (.NET 10 Worker Service) que mantiene records DNS A en Cloudflare
sincronizados con la IP pública dinámica.

**Cómo obtiene la IP pública** (con fallback):

1. Primario: API local de UniFi Dream Machine (Pro/SE), endpoint
   `/proxy/network/api/s/default/stat/health`, campo `wan_ip` del subsistema WAN.
2. Fallback: `api.ipify.org`.

**Cómo actualiza Cloudflare**: API REST oficial,
`PATCH https://api.cloudflare.com/client/v4/zones/{zoneId}/dns_records/{recordId}`.

**Arquitectura DNS recomendada**: en lugar de gestionar uno por uno cada subdominio,
mantener actualizados solo los **records apex** (`midominio.cl` tipo A). Todos los
subdominios que viven en casa (`home.midominio.cl`, `webhook.midominio.cl`,
`www.midominio.cl`, …) se configuran como CNAME al apex. Un solo cambio de IP propaga
a todos.

**Seguridad de credenciales**: los tokens de Cloudflare y UniFi se almacenan como
`PSCredential` XML cifrado con DPAPI (`Export-Clixml`). Solo el usuario Windows que
cifró el archivo, en la misma máquina, puede descifrarlo. Esto evita tokens en texto
plano.

### Configuración (User Secrets)

Tras `dotnet user-secrets set` desde `src/Phipes.Assistant.DdnsWorker/`:

| Setting                                    | Ejemplo / propósito                            |
|--------------------------------------------|------------------------------------------------|
| `Ddns:PollIntervalSeconds`                 | `300` (5 min).                                 |
| `Ddns:Cloudflare:ApiTokenPath`             | `C:\secrets\cloudflare-api.token.xml`          |
| `Ddns:Cloudflare:AccountId`                | Account ID de Cloudflare.                      |
| `Ddns:Cloudflare:Records:0:Zone`           | `midominio.cl`                                 |
| `Ddns:Cloudflare:Records:0:ZoneId`         | Zone ID de Cloudflare.                         |
| `Ddns:Cloudflare:Records:0:Hostname`       | `midominio.cl` (apex).                         |
| `Ddns:Cloudflare:Records:0:RecordId`       | Record ID del A apex.                          |
| `Ddns:Cloudflare:Records:0:Proxied`        | `false`                                        |
| `Ddns:Unifi:BaseUrl`                       | `https://192.168.1.1` (URL del UDM).           |
| `Ddns:Unifi:ApiTokenPath`                  | `C:\secrets\unifi-api.token.xml`               |
| `Ddns:Unifi:VerifyTls`                     | `false` para cert self-signed del UDM.         |
| `Ddns:Ipify:Endpoint`                      | `https://api.ipify.org` (fallback).            |

### Instalación como Windows Service

```powershell
dotnet publish -c Release -r win-x64 --self-contained false -o C:\Services\Phipes.Assistant.DdnsWorker

sc.exe create Phipes.Assistant.DdnsWorker `
    binPath= "C:\Services\Phipes.Assistant.DdnsWorker\Phipes.Assistant.DdnsWorker.exe" `
    start= auto `
    obj= "<dominio>\<usuario-que-cifro-los-XML>" `
    password= "<password>" `
    DisplayName= "Phipes Assistant DDNS Worker"

sc.exe start Phipes.Assistant.DdnsWorker
```

**Crítico**: `obj=` debe ser el mismo usuario Windows que cifró los XML DPAPI. Si no,
el servicio falla al descifrar. No usar `LocalSystem` ni `NetworkService`.

---

## Phipes.Assistant.WebhookHandler

ASP.NET Core (.NET 10) que recibe notificaciones de Microsoft Graph y orquesta la
respuesta del asistente. Corre como IIS site con **app pool always-on**.

### Componentes internos

| Componente                       | Función                                                                                            |
|----------------------------------|----------------------------------------------------------------------------------------------------|
| `POST /webhook/teams`            | Recibe notificaciones de chat y mail (mismo endpoint, ruteado por `Resource` type).                |
| `POST /webhook/teams/lifecycle`  | Recibe eventos `reauthorizationRequired`, `subscriptionRemoved`, `missed`.                         |
| `GET /health`                    | Healthcheck JSON simple.                                                                           |
| `NotificationDecrypter`          | RSA-OAEP-SHA1 + HMAC-SHA256 + AES-256-CBC (esquema oficial de Graph encrypted resource data).     |
| `JwtNotificationValidator`       | Valida `validationTokens[]` contra JWKS de Microsoft (modo `shadow` o `reject`).                  |
| `SqlIdempotencyStore`            | INSERT con PK `(SubscriptionId, ResourceId)` en MSSQL local; duplicate-key = dedupe.              |
| `TeamsNotificationHandler`       | Procesa mensajes de Teams chat → invoca Claude → postea reply via Graph.                          |
| `MailNotificationHandler`        | Procesa correos → filtra no-reply / no-en-TO → invoca Claude (que puede decidir `[SKIP]`).         |
| `LifecycleHandler`               | Renueva subscriptions al recibir `reauthorizationRequired`.                                        |
| `SubscriptionRenewer`            | `BackgroundService` que cada 30 min hace PATCH preventivo a las subscriptions configuradas.       |
| `ClaudeCodeInvoker`              | Spawn `claude.exe --print` con OAuth long-lived token + perfil personalizado.                     |
| `GraphTokenProvider`             | Maneja refresh + rotación del token OAuth de la app pública para llamadas Graph.                  |
| `WebhookAppTokenProvider`        | Idem para la "Webhook app" propia (la que tiene admin-consent para crear subscriptions).         |
| `FileLogger`                     | Centraliza logging a archivo. Path configurable via env var `HANDLER_LOG_DIR`.                    |

### Skills disponibles

El profile compartido en `<ClaudeHome>/.claude/skills/` define los skills que el
asistente descubre automáticamente:

| Skill                     | Hace                                                                  |
|---------------------------|-----------------------------------------------------------------------|
| `/handler-log`            | Lee/filtra el log diario del webhook handler.                         |
| `/subscriptions-status`   | Lista Graph subscriptions activas y tiempo de vida.                   |
| `/sql`                    | Consulta MSSQL local (tabla de idempotencia, etc.).                   |
| `/buscar-mail`            | Búsqueda Graph `$search` en el mailbox del usuario delegante.         |
| `/enviar-correo`          | Envía correo desde el asistente o crea draft en Borradores del jefe.  |
| `/leer-cal`               | Lista eventos del calendario del jefe en hora local.                  |
| `/agendar`                | Crea Teams meeting en el calendario del asistente, invita al resto.   |

### Modelo de identidad

El asistente es una **identidad real** en el tenant Microsoft 365 (mailbox + Teams +
calendar propios). El propietario humano le otorga:

- `FullAccess` sobre su mailbox (`Add-MailboxPermission`) → asistente puede leer y
  responder correos del jefe.
- `Editor` + `SharingPermissionFlags=Delegate` sobre la carpeta `Calendario` del jefe
  (`Add-MailboxFolderPermission`) → asistente puede leer su agenda.
- **NO** se otorga `Send-As`: el asistente nunca suplanta al jefe en envíos. Cuando
  responde mails del jefe, envía desde su propio mailbox con CC al jefe; cuando agenda
  reuniones, las crea en su propio calendario invitando al jefe como required.

---

## Requisitos

- Windows Server (probado en 2022) o Windows 11.
- .NET 10 SDK (versión exacta en `global.json`).
- IIS con módulo ASP.NET Core (in-process hosting) y **Application Initialization
  feature** instalada (`Install-WindowsFeature Web-AppInit`).
- PowerShell 7 (`pwsh.exe`) o Git for Windows, requerido por Claude Code en Windows.
- SQL Server (cualquier edición, incluso Express; probado en 2022) para la tabla de
  idempotencia.
- Cuenta Microsoft 365 dedicada para el asistente, con licencia que incluya Exchange
  Online y Teams (Business Basic alcanza).
- Acceso a Entra ID (Azure AD) para crear una **App Registration** con permisos
  delegados.
- Suscripción Claude (Anthropic) para Claude Code. El plan Max permite uso headless.
- Cuenta Cloudflare con dominio gestionado (solo si va a usar el DdnsWorker).

---

## Setup completo del WebhookHandler — paso a paso

Asumimos su dominio se llama `midominio.cl`, el asistente es `asistente@midominio.cl`,
y el jefe es `usted@midominio.cl`. Sustituya con sus valores reales.

### 1. Entra App Registration para webhooks con encryption

Crear una app registration nueva en Entra (Azure AD → App registrations → New).

Permisos delegados a otorgar (y dar **admin consent** desde Entra):
- `Chat.ReadWrite`
- `Chat.ReadWrite.All` (requerido para subscriptions a `/me/chats/getAllMessages`)
- `Mail.ReadWrite`
- `Mail.Send`
- `Calendars.ReadWrite`
- `User.Read`
- `offline_access`

Anote el **Application (client) ID** y el **Tenant ID**.

### 2. Autenticar al asistente con esa app (device code flow una vez)

Desde una PowerShell en cualquier máquina con browser:

```powershell
$tenantId = '<tu-tenant-id>'
$clientId = '<application-id-de-la-app-arriba>'
$scopes   = 'openid profile offline_access User.Read Mail.ReadWrite Mail.Send Chat.ReadWrite Chat.ReadWrite.All Calendars.ReadWrite'

# Device code flow
$dc = Invoke-RestMethod -Method POST `
    -Uri "https://login.microsoftonline.com/$tenantId/oauth2/v2.0/devicecode" `
    -Body @{ client_id=$clientId; scope=$scopes }

"Abra: $($dc.verification_uri)"
"Code: $($dc.user_code)"

# Esperar a que el usuario firme y luego:
Start-Sleep -Seconds 30
$tk = Invoke-RestMethod -Method POST `
    -Uri "https://login.microsoftonline.com/$tenantId/oauth2/v2.0/token" `
    -Body @{
        grant_type    = 'urn:ietf:params:oauth:grant-type:device_code'
        client_id     = $clientId
        device_code   = $dc.device_code
    }

# Guardar el refresh_token cifrado con DPAPI
$rt  = $tk.refresh_token
$cred = New-Object PSCredential('asistente@midominio.cl', (ConvertTo-SecureString $rt -AsPlainText -Force))
$cred | Export-Clixml -Path 'C:\secrets\asistente-refresh.cred.xml'
```

**Importante**: Microsoft rota el `refresh_token` en cada uso. El handler maneja la
rotación y reescribe el XML automáticamente. Si dos procesos consumen el mismo XML al
mismo tiempo, se desincronizan — designe **un único** consumidor por XML.

### 3. Certificado de encryption para resource data

Las subscriptions con `includeResourceData=true` requieren un certificado RSA-2048 que
Microsoft usa para cifrar el contenido de cada notificación. Generar uno self-signed:

```powershell
$cert = New-SelfSignedCertificate `
    -Subject 'CN=phipes-webhook-encryption' `
    -KeyAlgorithm RSA -KeyLength 2048 `
    -KeyUsage KeyEncipherment, DataEncipherment `
    -NotAfter (Get-Date).AddYears(2) `
    -CertStoreLocation 'Cert:\CurrentUser\My'

# Exportar PFX con password
$pwd = ConvertTo-SecureString '<password-aleatorio>' -AsPlainText -Force
Export-PfxCertificate -Cert $cert -FilePath 'C:\secrets\webhook-encryption.pfx' -Password $pwd

# El "encryption certificate" que se manda a Microsoft es el DER del public key, base64
$pubDer = $cert.GetRawCertData()
$b64    = [Convert]::ToBase64String($pubDer)
"$b64"  # → este string va al campo encryptionCertificate al crear la subscription
```

### 4. Delegación FullAccess sobre el mailbox del jefe

Vía Exchange Online PowerShell, una sola vez:

```powershell
Connect-ExchangeOnline
Add-MailboxPermission `
    -Identity 'usted@midominio.cl' `
    -User 'asistente@midominio.cl' `
    -AccessRights FullAccess `
    -InheritanceType All `
    -AutoMapping $true

Add-MailboxFolderPermission `
    -Identity 'usted@midominio.cl:\Calendario' `
    -User 'asistente@midominio.cl' `
    -AccessRights Editor `
    -SharingPermissionFlags Delegate
```

Si su calendario está en inglés use `\Calendar`.

### 5. Base de datos MSSQL

Como administrador SQL:

```sql
CREATE DATABASE PhipesAssistant;
GO

USE PhipesAssistant;
GO

CREATE TABLE WebhookProcessedMessages (
    SubscriptionId  NVARCHAR(64)  NOT NULL,
    ResourceId      NVARCHAR(512) NOT NULL,
    ProcessedAt     DATETIME2     NOT NULL DEFAULT SYSUTCDATETIME(),
    Channel         NVARCHAR(16)  NOT NULL,
    CONSTRAINT PK_WebhookProcessedMessages PRIMARY KEY CLUSTERED (SubscriptionId, ResourceId)
);
CREATE INDEX IX_WebhookProcessedMessages_ProcessedAt ON WebhookProcessedMessages(ProcessedAt);

-- Login Windows para la cuenta del app pool de IIS
CREATE LOGIN [DOMINIO\svc-cuenta-app-pool] FROM WINDOWS;
USE PhipesAssistant;
CREATE USER [DOMINIO\svc-cuenta-app-pool] FOR LOGIN [DOMINIO\svc-cuenta-app-pool];
ALTER ROLE db_datareader ADD MEMBER [DOMINIO\svc-cuenta-app-pool];
ALTER ROLE db_datawriter ADD MEMBER [DOMINIO\svc-cuenta-app-pool];
```

### 6. Claude Code en el servidor

Descargar `claude.exe` (Windows binary) y ponerlo en `C:\Tools\Claude\`.

```powershell
# Como el usuario humano con suscripción Claude, una sola vez:
claude setup-token
# Genera un token long-lived (1 año) que aparece una sola vez en consola.
# Guárdelo cifrado con DPAPI:

$token = '<el-token-sk-ant-oat01-...>'
$cred = New-Object PSCredential('claude-code', (ConvertTo-SecureString $token -AsPlainText -Force))
$cred | Export-Clixml -Path 'C:\secrets\claude-oauth.cred.xml'
```

Crear un **perfil dedicado** para el asistente en cualquier path (ej.
`C:\AsistenteProfile\`) y dentro el subdirectorio `.claude/` con:

- `CLAUDE.md` — system prompt global del asistente (identidad, reglas, tono).
- `skills/` — copiar los `SKILL.md` de cada skill desde el repo o crearlos.
- `settings.json` con los permisos pre-aprobados (modo headless no puede mostrar
  prompts interactivos):

```json
{
  "permissions": {
    "allow": [
      "WebFetch",
      "WebSearch",
      "PowerShell(*)",
      "Read(**)",
      "Glob(**)",
      "Grep(**)",
      "Write(**)",
      "Edit(**)"
    ]
  }
}
```

> **En Windows el tool de shell se llama `PowerShell`, NO `Bash`**. Esto es un detalle
> esencial — `Bash(*)` no matchea nada en Windows.

### 7. Configurar IIS site

```powershell
Import-Module WebAdministration

# Crear app pool con identity de service account dedicada
$apName = 'phipes.assistant'
New-WebAppPool -Name $apName
Set-ItemProperty "IIS:\AppPools\$apName" -Name processModel -Value @{
    identityType = 'SpecificUser'
    userName     = 'DOMINIO\svc-cuenta-app-pool'
    password     = '<password-del-svc>'
}

# Always-on (sin esto, el BackgroundService muere por idle)
& "$env:windir\system32\inetsrv\appcmd.exe" set apppool $apName /startMode:AlwaysRunning
& "$env:windir\system32\inetsrv\appcmd.exe" set apppool $apName /processModel.idleTimeout:00:00:00
& "$env:windir\system32\inetsrv\appcmd.exe" set apppool $apName /recycling.periodicRestart.time:00:00:00

# Crear el site (asume binding a webhook.midominio.cl con cert TLS)
New-Website -Name 'phipes.assistant' `
    -PhysicalPath 'C:\inetpub\phipes.assistant' `
    -ApplicationPool $apName `
    -HostHeader 'webhook.midominio.cl' `
    -Port 443 -Ssl

& "$env:windir\system32\inetsrv\appcmd.exe" set site 'phipes.assistant' /serverAutoStart:true

# Variables de entorno del app pool (paths de logs y sessions)
Add-WebConfigurationProperty -PSPath 'MACHINE/WEBROOT/APPHOST' `
    -Filter "system.applicationHost/applicationPools/add[@name='$apName']/environmentVariables" `
    -Name "." -Value @{ name='HANDLER_LOG_DIR'; value='C:\inetpub\phipes.assistant\logs' }

Add-WebConfigurationProperty -PSPath 'MACHINE/WEBROOT/APPHOST' `
    -Filter "system.applicationHost/applicationPools/add[@name='$apName']/environmentVariables" `
    -Name "." -Value @{ name='HANDLER_SESSIONS_DIR'; value='C:\inetpub\phipes.assistant\sessions' }
```

ACL sobre `C:\inetpub\phipes.assistant\`: `DOMINIO\svc-cuenta-app-pool` con Modify.

### 8. User Secrets del WebhookHandler

Tras `dotnet user-secrets set` desde `src/Phipes.Assistant.WebhookHandler/` (corriendo
como la cuenta del app pool, no como el dev — porque ese perfil es donde IIS busca los
secrets en producción):

```powershell
# Webhook (estado compartido entre creator de subscriptions y handler)
dotnet user-secrets set "Webhook:ClientState" "<random-string-largo-unico>"

# Encryption (PFX + password + identificador)
dotnet user-secrets set "Encryption:PfxPath"      "C:\secrets\webhook-encryption.pfx"
dotnet user-secrets set "Encryption:PfxPassword"  "<password-del-pfx>"
dotnet user-secrets set "Encryption:CertificateId" "phipes-webhook-202605"

# Graph (token delegated, identidad de la asistente)
dotnet user-secrets set "Graph:TenantId"           "<tenant-id>"
dotnet user-secrets set "Graph:ClientId"           "<app-id-pública-microsoft-graph-powershell>"
dotnet user-secrets set "Graph:RefreshTokenPath"   "C:\secrets\asistente-refresh.cred.xml"
dotnet user-secrets set "Graph:UserPrincipalName"  "asistente@midominio.cl"
dotnet user-secrets set "Graph:Scopes"             "openid offline_access User.Read Mail.ReadWrite Mail.Send Chat.ReadWrite Calendars.ReadWrite"

# WebhookApp (token con admin-consent para administrar subscriptions)
dotnet user-secrets set "WebhookApp:TenantId"          "<tenant-id>"
dotnet user-secrets set "WebhookApp:ClientId"          "<application-id-de-tu-app-registration>"
dotnet user-secrets set "WebhookApp:RefreshTokenPath"  "C:\secrets\asistente-refresh-webhookapp.cred.xml"
dotnet user-secrets set "WebhookApp:UserPrincipalName" "asistente@midominio.cl"

# Renewer (auto-renovación de subscriptions)
dotnet user-secrets set "Renewer:IntervalMinutes"          "30"
dotnet user-secrets set "Renewer:RenewWhenLessThanMinutes" "30"
dotnet user-secrets set "Renewer:ChatExtendMinutes"        "55"
dotnet user-secrets set "Renewer:MailExtendMinutes"        "4230"
dotnet user-secrets set "Renewer:SubscriptionIds:0"        "<id-de-tu-sub-chat>"
dotnet user-secrets set "Renewer:SubscriptionIds:1"        "<id-de-tu-sub-mail>"

# Idempotency (MSSQL)
dotnet user-secrets set "Idempotency:ConnectionString" "Server=.;Database=PhipesAssistant;Integrated Security=True;Encrypt=False;TrustServerCertificate=True"

# Claude Code
dotnet user-secrets set "Claude:ExePath"             "C:\Tools\Claude\claude.exe"
dotnet user-secrets set "Claude:OAuthTokenPath"      "C:\secrets\claude-oauth.cred.xml"
dotnet user-secrets set "Claude:ClaudeHome"          "C:\AsistenteProfile"
dotnet user-secrets set "Claude:MaxBudgetUsd"        "0.50"
dotnet user-secrets set "Claude:TimeoutSeconds"      "300"
dotnet user-secrets set "Claude:AppendSystemPrompt"  "<prompt-extra-con-lista-de-skills>"

# Security (validación JWT)
dotnet user-secrets set "Security:RejectInvalidJwts" "false"   # true en producción tras validación
```

### 9. Crear las subscriptions de Graph

Una vez la URL pública del webhook (`https://webhook.midominio.cl/webhook/teams`) esté
arriba y respondiendo 200, crear las subscriptions:

```powershell
# Refrescar access token con la app que tiene Chat.ReadWrite.All admin-consent
$rt = (Import-Clixml 'C:\secrets\asistente-refresh-webhookapp.cred.xml').GetNetworkCredential().Password
$resp = Invoke-RestMethod -Method POST `
    -Uri "https://login.microsoftonline.com/<tenant-id>/oauth2/v2.0/token" `
    -Body @{
        grant_type='refresh_token'
        client_id='<app-id-de-tu-webhookapp>'
        refresh_token=$rt
        scope='openid offline_access User.Read Chat.ReadWrite Chat.ReadWrite.All'
    } -ContentType 'application/x-www-form-urlencoded'

# Persistir el refresh rotado
if ($resp.refresh_token -and $resp.refresh_token -ne $rt) {
    (New-Object PSCredential('asistente@midominio.cl', (ConvertTo-SecureString $resp.refresh_token -AsPlainText -Force))) |
        Export-Clixml -Path 'C:\secrets\asistente-refresh-webhookapp.cred.xml'
}

$hdr = @{ Authorization = "Bearer $($resp.access_token)" }

# Encryption certificate (public key DER base64)
$encB64 = '<base64-del-public-key-de-tu-cert>'
$encId  = 'phipes-webhook-202605'

# Subscription Teams chat (max 60 min, renovar con SubscriptionRenewer)
$body = @{
    changeType                = 'created'
    notificationUrl           = 'https://webhook.midominio.cl/webhook/teams'
    lifecycleNotificationUrl  = 'https://webhook.midominio.cl/webhook/teams/lifecycle'
    resource                  = '/me/chats/getAllMessages'
    expirationDateTime        = (Get-Date).ToUniversalTime().AddMinutes(55).ToString('o')
    clientState               = '<el-clientState-que-pusiste-en-User-Secrets>'
    includeResourceData       = $true
    latestSupportedTlsVersion = 'v1_2'
    encryptionCertificate     = $encB64
    encryptionCertificateId   = $encId
} | ConvertTo-Json -Depth 10 -Compress

Invoke-RestMethod -Method POST `
    -Uri 'https://graph.microsoft.com/v1.0/subscriptions' `
    -Headers $hdr -Body $body -ContentType 'application/json'

# Subscription Mail (max 4230 min = 70h, también renovable)
# Idem al anterior pero con resource = '/me/messages?$select=subject,from,toRecipients,bodyPreview,receivedDateTime,isRead'
```

Anote los `id` retornados y agréguelos a `Renewer:SubscriptionIds`.

### 10. Build + Deploy

```powershell
dotnet build src/Phipes.Assistant.WebhookHandler -c Release
dotnet publish src/Phipes.Assistant.WebhookHandler -c Release -o C:\inetpub\phipes.assistant
# Reciclar el app pool
Restart-WebAppPool 'phipes.assistant'
```

Validación: `curl https://webhook.midominio.cl/health` debe responder JSON 200.

---

## Estructura de directorios runtime

```
C:\inetpub\phipes.assistant\        ← IIS site root, código del WebhookHandler
├── Phipes.Assistant.WebhookHandler.dll
├── … (deps)
├── runtimes\win-x64\native\        ← DLLs nativas de Microsoft.Data.SqlClient
├── logs\                           ← archivos de log diarios (HANDLER_LOG_DIR)
└── sessions\                       ← flag files de continuidad por chat (HANDLER_SESSIONS_DIR)

C:\AsistenteProfile\                ← profile del asistente (USERPROFILE forzado)
└── .claude\
    ├── CLAUDE.md                   ← identidad y reglas globales del asistente
    ├── memory\                     ← memorias persistentes (markdown)
    ├── skills\                     ← skills (markdown) descubiertos automáticamente
    ├── settings.json               ← permisos del modo headless
    ├── .credentials.json           ← OAuth de Claude Code (auto-gestionado)
    └── sessions\, projects\, …     ← state interno

C:\secrets\                         ← tokens cifrados con DPAPI
├── asistente-refresh.cred.xml
├── asistente-refresh-webhookapp.cred.xml
├── claude-oauth.cred.xml
├── webhook-encryption.pfx
├── cloudflare-api.token.xml        (DdnsWorker)
└── unifi-api.token.xml             (DdnsWorker)
```

---

## Lecciones aprendidas

Cosas no obvias que cuestan al implementar:

1. **`Bash(*)` no funciona en Windows**. El tool de shell que Claude Code expone se
   llama **`PowerShell`** en Windows. Allowlist debe ser `PowerShell(*)`, no
   `Bash(...)`.

2. **Microsoft rota refresh tokens en cada uso**. Si dos procesos comparten el mismo
   `*.cred.xml`, se desincronizan y uno empieza a fallar con `invalid_grant`. Designe
   un único consumidor por XML.

3. **`includeResourceData=true` requiere `$select` en mail subscriptions**. Sin
   `$select` Microsoft devuelve `400: select clause should be present in query string
   to support rich notifications`.

4. **El `id` del recurso NO viene en el JSON descifrado cuando se usa `$select`** sin
   incluir `id` explícito. Hay que extraerlo del `notification.Resource` URL con
   regex: `messages\(['"]([^'"]+)['"]\)`.

5. **Cada app context ve solo sus propias subscriptions**. Una "public client app" de
   Microsoft no puede listar/PATCH subscriptions creadas por tu app registration. El
   `SubscriptionRenewer` debe usar el token de la **misma app** que creó las
   subscriptions.

6. **Microsoft entra en "silencio temporal"** a veces — deja de entregar notifications
   aunque la subscription siga activa. El fix es hacer PATCH a `expirationDateTime`
   para "tocar" la sub; eso reactiva el delivery. El `SubscriptionRenewer` aquí hace
   PATCH preventivo cada 30 min para prevenir el caso.

7. **ASP.NET Core in-process hosting NO flushea `ILogger` al stdout log de IIS**. Los
   logs se pierden si no se configura un sink alternativo. La clase `FileLogger` en
   este repo escribe a un archivo diario que sobrevive a reinicios del pool.

8. **IIS in-process hosting requiere always-on** (`startMode=AlwaysRunning`,
   `idleTimeout=0`, `serverAutoStart=true`) para que el `BackgroundService` no muera
   por idle del app pool.

9. **PFX cargado con `MachineKeySet | PersistKeySet`** vive en el HKLM, accesible para
   la identidad del app pool. Sin esos flags el cert solo está disponible para el user
   que lo cargó.

10. **`Send-As` no es necesario** (y no se recomienda). Cuando el asistente responde
    correos del jefe envía desde su propia identidad con CC al jefe. Cuando agenda
    reuniones las crea en su propio calendario invitando al jefe. Esto da trazabilidad
    clara de quién hizo qué.

11. **Cuando `Get-Content -Wait` se usa para tailar un log, validar fecha UTC**. El
    archivo rota por fecha UTC (no local) — un tail local que no recalcula la fecha UTC
    se queda atado al archivo de "ayer" después de medianoche UTC.

---

## Seguridad

- **Tokens nunca en texto plano**. Todos cifrados con DPAPI (scope `CurrentUser`).
- **Refresh tokens dedicados por consumidor**. No se comparten XMLs entre procesos.
- **`clientState`** es un secreto compartido entre el creador de la subscription y el
  handler. Debe ser un string aleatorio largo (>32 chars). Vive en User Secrets, nunca
  en código.
- **Validación JWT** de `validationTokens[]` contra JWKS de Microsoft, previene que un
  atacante que descubra la URL del webhook inyecte notificaciones falsas (modo `shadow`
  por default, activable a `reject` desde `Security:RejectInvalidJwts`).
- **HMAC-SHA256** sobre el ciphertext de cada notification, verificado antes de
  descifrar — previene tampering.
- **Idempotencia transaccional** con PK clustered en MSSQL, descarta duplicados de
  Microsoft sin re-procesar.
- **Scopes mínimos**. `Chat.ReadWrite.All` solo en la app dedicada a manejar
  subscriptions; resto de operaciones usan `Chat.ReadWrite` delegated.

---

## Solución de problemas

| Síntoma                                                       | Diagnóstico                                                                                   |
|---------------------------------------------------------------|-----------------------------------------------------------------------------------------------|
| Webhook recibe `POST` 202 pero no responde nada               | Verificar `<HANDLER_LOG_DIR>\handler-yyyymmdd.log` — el procesamiento es async (Task.Run).    |
| `Descartadas N notifications por clientState invalido`        | El `Webhook:ClientState` en User Secrets no matchea el de la subscription. Recrear sub.       |
| `invalid_grant` al refrescar token                            | Refresh token rotó en otro proceso. Re-provisionar XML con device code flow.                  |
| `Subscription validation request failed. HTTP 'NotFound'`     | Site IIS está `Stopped` (no el app pool). Verificar con `Get-Website`.                        |
| `Notification endpoint must respond with 200 OK`              | Misma causa que arriba; o el cert TLS no es válido externamente.                              |
| `MSDTC has cancelled` o errores raros de MSSQL                | Faltan DLLs nativas de SqlClient — copiar recursivamente `publish/runtimes/win-x64/native/`.  |
| `claude.exe Error: Input must be provided`                    | Modo headless detectó que no hay TTY. No usar `--remote-control` en scheduled task.           |
| Sarah dice "no tengo acceso al mailbox"                       | El `--append-system-prompt` no mencionaba skills, o el settings.json no permite `PowerShell`. |

---

## Licencia

MIT. Vea `LICENSE`. Manteniendo el aviso de copyright, úselo, modifíquelo, comercialícelo,
o adáptelo a su entorno.
