# Estrutura de desenvolvimento e VM de homologacao

## Objetivo

Este runbook orienta agentes de codigo e desenvolvedores humanos a entenderem a
estrutura atual do projeto, rodarem validacoes locais e acessarem a VM Windows
usada para homologacao do SyncAgent.

Nao grave senhas reais neste arquivo. Use variaveis de ambiente locais ou um
cofre de credenciais da maquina.

## Repositorios envolvidos

| Caminho | Papel |
| --- | --- |
| `D:\GitHub\pdv-local` | Implementacao do SyncAgent, instalador, tray, scripts Windows e docs operacionais. |
| `D:\GitHub\erp` | ERP Django, Sync API, geracao de codigo de ativacao e tela de plano/conexoes. |
| `D:\GitHub\sync` | Fonte de verdade dos contratos entre ERP, PDV e Sync. |
| `D:\GitHub\skills-shared` | Skills compartilhadas entre ERP, PDV e Sync. |

Regra: contratos e eventos devem ser alterados primeiro em `D:\GitHub\sync`.
Codigo de implementacao deve ficar em `erp` ou `pdv-local`, conforme o sistema.

## Estrutura do PDV Local

| Caminho | Conteudo |
| --- | --- |
| `src\sync-agent` | Worker .NET 8 do SyncAgent. |
| `src\pdv-core` | Biblioteca .NET 8 com acesso ao banco local do PDV, validacoes e repositorios. |
| `src\pdv-app` | Aplicacao WPF do PDV Local. |
| `src\sync-agent-installer` | Instalador interativo Windows. |
| `src\sync-agent-tray` | Aplicativo de bandeja do Windows. |
| `tests\pdv-core-tests` | Testes automatizados da camada de dados do PDV. |
| `infra\windows` | Scripts de instalacao, empacotamento, bootstrap e validacao. |
| `infra\postgres\init` | SQL base do PostgreSQL local do SyncAgent. |
| `infra\arpa` | Templates e SQL da integracao Arpa read-only. |
| `docs` | Runbooks, decisoes tecnicas e manuais da Sprint 1. |
| `artifacts` | Saidas geradas localmente por build/publish/download. Nao tratar como fonte de verdade. |

## Estrutura do ERP relacionada ao Sync

| Caminho | Conteudo |
| --- | --- |
| `sync_api` | API Django de sincronizacao, ativacao de agentes e modelos Sync. |
| `core\templates\padrao\admin\configuracoes_plano.html` | Tela somente leitura do plano recebido do CP. |
| `core\admin_views.py` | Views administrativas de Conexoes, Plano e outras configuracoes. |
| `docs\infra` | Documentacao tecnica do ERP relacionada a infra e Sync API. |

## Contratos

O diretorio `D:\GitHub\sync` e a fonte de verdade para:

- OpenAPI do ERP;
- schemas JSON de eventos;
- exemplos oficiais de request/response;
- changelog de versoes de contrato.

Ao alterar comunicacao entre SyncAgent e ERP:

1. Verifique o contrato em `D:\GitHub\sync`.
2. Atualize exemplos se houver mudanca.
3. Aplique a implementacao no ERP e/ou PDV Local.
4. Rode testes dos dois lados quando a mudanca tocar integracao.

## Comandos locais principais

### Build do SyncAgent

```powershell
dotnet build D:\GitHub\pdv-local\src\sync-agent\SyncAgent.csproj
```

### Testes do PDV Core

```powershell
dotnet test D:\GitHub\pdv-local\tests\pdv-core-tests\PdvLocal.Core.Tests.csproj
```

Smoke opcional com banco PostgreSQL real:

```powershell
$env:PDV_LOCAL_TEST_CONNECTION_STRING="Host=localhost;Port=5432;Database=pdv_sync;Username=pdv_sync;Password=pdv_sync"
dotnet test D:\GitHub\pdv-local\tests\pdv-core-tests\PdvLocal.Core.Tests.csproj --filter PdvDatabaseIntegrationTests
Remove-Item Env:\PDV_LOCAL_TEST_CONNECTION_STRING
```

### Publish Release do SyncAgent

```powershell
$publishDir = 'D:\GitHub\pdv-local\artifacts\sync-agent\manual-publish'
if (Test-Path -LiteralPath $publishDir) {
    Remove-Item -LiteralPath $publishDir -Recurse -Force
}
dotnet publish D:\GitHub\pdv-local\src\sync-agent\SyncAgent.csproj -c Release -o $publishDir
```

### Build do instalador

```powershell
dotnet build D:\GitHub\pdv-local\src\sync-agent-installer\SyncAgent.Installer.csproj -c Release
```

### Build do tray

```powershell
dotnet build D:\GitHub\pdv-local\src\sync-agent-tray\SyncAgent.Tray.csproj -c Release
```

### Check do ERP

Use sempre o `venv` do ERP:

```powershell
D:\GitHub\erp\venv\Scripts\python.exe D:\GitHub\erp\manage.py check
```

### Testes Sync API do ERP

```powershell
D:\GitHub\erp\venv\Scripts\python.exe D:\GitHub\erp\manage.py test sync_api.tests -v 2 --keepdb
```

Para o bloco de ativacao/plano:

```powershell
D:\GitHub\erp\venv\Scripts\python.exe D:\GitHub\erp\manage.py test sync_api.tests.SyncActivationAdminTests -v 2 --keepdb
```

## VM Windows de homologacao

> **Atualizado em 2026-09-10:** a VM abaixo ja foi reinstalada com a
> nomenclatura nova (`C:\Program Files\AraraSuite.com.br\` com `PDV\` e
> `Sync\Agent\`/`Sync\Tray\` como subpastas, servico `AraraSuiteSync`). A
> tabela abaixo reflete o estado real observado nessa data — confirme com
> `Get-Service` antes de assumir que nao mudou de novo.

| Item | Valor atual |
| --- | --- |
| Nome observado | `ERP` |
| IP | `192.168.0.184` |
| Protocolo | WinRM HTTP |
| Porta | `5985` |
| Usuario operacional | `Suporte` |
| Servico SyncAgent (nesta VM) | `AraraSuiteSync` (nome de exibicao `AraraSuite Sync`) |
| Instalacao SyncAgent (nesta VM) | `C:\Program Files\AraraSuite.com.br\Sync\Agent` |
| Dashboard local na VM | `http://127.0.0.1:47891/` |
| Banco local SyncAgent | PostgreSQL 17 + pgvector 0.8.0 |
| ERP ativado (2026-09-10) | `https://demo.ararasuite.com.br` (tenant `507f781b-57c2-4c91-9ff4-0ffe89aff235`) — nao mais o Docker integrado abaixo; validar via `/status` antes de assumir. |
| Conexao Arpa configurada | `"Geral"`, id `e869c02b89ac45238f4ab5304962f584` (ver `GET /config/arpa`). |

## Ambiente Docker integrado CP + ERP

O ambiente integrado de desenvolvimento fica em `D:\GitHub\erp-control-plane`:

```text
docker-compose.integrated-dev.yml
scripts\dev\restore-erp-development-to-integrated-dev.ps1
```

Servicos e portas atuais:

| Servico | Porta host | Uso |
| --- | --- | --- |
| `cp_web` | `8001` | Control Plane local. |
| `cp_db` | `5433` | PostgreSQL do Control Plane. |
| `cp_redis` | `6380` | Redis do Control Plane. |
| `erp_cliente_web` | `8002` | ERP do cliente `desenvolvimento`. |
| `erp_cliente_db` | `5434` | PostgreSQL do cliente `desenvolvimento`. |
| `erp_cliente_redis` | `6381` | Redis do ERP cliente. |

Dados do banco ERP cliente no Docker:

```text
Host: 192.168.0.31
Porta: 5434
Database: erp_desenvolvimento
Usuario: erp_desenvolvimento
Senha dev: erp_desenvolvimento_dev
```

O primeiro cliente do CP usa:

```text
Slug: desenvolvimento
Nome: Desenvolvimento
ERP: http://192.168.0.31:8002
```

Em algum momento o SyncAgent da VM `192.168.0.184` ja foi ativado contra esse
ERP com `InstanceId: syncagent-vm-erp`, `TenantId:
f39436d5-d521-47a2-9fa9-b0a75d98d399`, `ERP API: http://192.168.0.31:8002`.
**Isso nao reflete mais o estado atual** — em 2026-09-10 a VM estava ativada
contra `https://demo.ararasuite.com.br` (ver tabela da VM acima). Confirme
sempre via `GET /status` antes de assumir contra qual ERP a VM esta
apontada; reativar contra o docker integrado exige gerar um novo codigo de
ativacao nesse ERP e repetir o fluxo de `/setup`.

Para acesso externo ao Docker Desktop, liberar no Windows da maquina
`192.168.0.31`:

```powershell
New-NetFirewallRule `
    -DisplayName "Arara Integrated Dev PostgreSQL 5434" `
    -Direction Inbound `
    -Action Allow `
    -Protocol TCP `
    -LocalPort 5434

New-NetFirewallRule `
    -DisplayName "Arara Integrated Dev ERP 8002" `
    -Direction Inbound `
    -Action Allow `
    -Protocol TCP `
    -LocalPort 8002
```

O SyncAgent permite HTTP em `localhost` e redes privadas RFC1918 apenas para
ambiente de desenvolvimento/homologacao local. Fora disso, a API do ERP deve
usar HTTPS.

Senha: nao versionar. Antes de conectar, defina em uma sessao PowerShell local:

```powershell
$env:SYNC_VM_PASSWORD = '<senha-do-usuario-Suporte>'
```

Para persistir a senha no perfil do usuario Windows da maquina local, sem
gravar no repositorio:

```powershell
[Environment]::SetEnvironmentVariable(
    "SYNC_VM_PASSWORD",
    "<senha-do-usuario-Suporte>",
    "User"
)
```

Depois abra uma nova janela do PowerShell e valide:

```powershell
if ([string]::IsNullOrWhiteSpace($env:SYNC_VM_PASSWORD)) {
    throw "SYNC_VM_PASSWORD nao configurada."
}
```

## Conectar na VM por WinRM

```powershell
$sec = ConvertTo-SecureString $env:SYNC_VM_PASSWORD -AsPlainText -Force
$cred = New-Object System.Management.Automation.PSCredential('Suporte', $sec)

Invoke-Command -ComputerName 192.168.0.184 -Credential $cred -Authentication Basic -ScriptBlock {
    hostname
    Get-Service 'AraraSuiteSync'
}
```

Se o cliente WinRM local ainda nao estiver configurado, executar PowerShell como
Administrador na maquina local:

```powershell
winrm quickconfig -force
Set-Service WinRM -StartupType Automatic
Start-Service WinRM
Set-Item WSMan:\localhost\Client\TrustedHosts -Value "192.168.0.184" -Concatenate -Force
Set-Item WSMan:\localhost\Client\AllowUnencrypted -Value $true
Set-Item WSMan:\localhost\Client\Auth\Basic -Value $true
```

Na VM, o WinRM deve estar ativo:

```powershell
winrm quickconfig -force
Enable-PSRemoting -Force
Set-Item WSMan:\localhost\Service\Auth\Basic -Value $true
Set-Item WSMan:\localhost\Service\AllowUnencrypted -Value $true
netstat -ano | findstr ":5985"
```

## Validar instalacao ativa na VM

```powershell
$sec = ConvertTo-SecureString $env:SYNC_VM_PASSWORD -AsPlainText -Force
$cred = New-Object System.Management.Automation.PSCredential('Suporte', $sec)

Invoke-Command -ComputerName 192.168.0.184 -Credential $cred -Authentication Basic -ScriptBlock {
    $service = Get-Service 'AraraSuiteSync'
    $root = Invoke-WebRequest -Uri 'http://127.0.0.1:47891/' -UseBasicParsing -TimeoutSec 10
    $status = Invoke-WebRequest -Uri 'http://127.0.0.1:47891/status' -UseBasicParsing -TimeoutSec 10

    [pscustomobject]@{
        ServiceStatus = $service.Status.ToString()
        DashboardHttp = $root.StatusCode
        StatusHttp = $status.StatusCode
    }
}
```

Resultado esperado:

- `ServiceStatus = Running`
- `DashboardHttp = 200`
- `StatusHttp = 200`

## Atualizar appsettings.json na VM com backup

Exemplo para alterar a conexao read-only do Arpa:

```powershell
$sec = ConvertTo-SecureString $env:SYNC_VM_PASSWORD -AsPlainText -Force
$cred = New-Object System.Management.Automation.PSCredential('Suporte', $sec)

Invoke-Command -ComputerName 192.168.0.184 -Credential $cred -Authentication Basic -ScriptBlock {
    $ErrorActionPreference = 'Stop'

    $path = 'C:\Program Files\AraraSuite.com.br\Sync\Agent\appsettings.json'
    $backup = "$path.bak-$(Get-Date -Format yyyyMMdd-HHmmss)"
    Copy-Item -LiteralPath $path -Destination $backup -Force

    $json = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
    $json.ArpaCollector.ConnectionString = 'Host=192.168.0.31;Port=5432;Database=control;Username=sync_agent_anapolis_ro'
    $json | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $path -Encoding UTF8

    Restart-Service 'AraraSuiteSync' -Force
    Start-Sleep -Seconds 4

    [pscustomobject]@{
        Backup = $backup
        ServiceStatus = (Get-Service 'AraraSuiteSync').Status.ToString()
        ConnectionString = (Get-Content -LiteralPath $path -Raw | ConvertFrom-Json).ArpaCollector.ConnectionString
    }
}
```

## Publicar nova build do SyncAgent na VM

Este fluxo preserva o `appsettings.json`/`appsettings.Development.json`
instalados.

> **Pegadinha confirmada em 2026-09-10:** `dotnet publish` gera uma pasta
> `runtimes\win\lib\net8.0\` com `System.Diagnostics.EventLog.dll`,
> `System.Diagnostics.EventLog.Messages.dll` e
> `System.ServiceProcess.ServiceController.dll` especificas do RID `win`.
> O `.deps.json` resolve `System.ServiceProcess.ServiceController` **so**
> por esse caminho RID-specific — uma copia solta na raiz do install dir
> nao e suficiente. Sem essa pasta o servico Windows sobe e crasha na
> hora com `System.IO.FileNotFoundException` (visto no Event Log,
> provider `.NET Runtime`, id 1026) ao chamar `AddWindowsService`. Copie
> `runtimes\win\lib\net8.0\*.dll` para
> `<installDir>\runtimes\win\lib\net8.0\` junto com os binarios soltos.
>
> Se a ferramenta que estiver rodando o deploy bloquear `Remove-Item` em
> caminhos `C:\Program Files\...` (classificador de seguranca local), use
> uma pasta de staging remota com timestamp exclusivo em vez de apagar a
> anterior, e substitua os arquivos existentes por `Copy-Item -Force` em
> vez de `Remove-Item` + `Copy-Item` recursivo.

```powershell
$publishDir = 'D:\GitHub\pdv-local\artifacts\sync-agent\manual-publish'
dotnet publish D:\GitHub\pdv-local\src\sync-agent\SyncAgent.csproj -c Release -o $publishDir

$sec = ConvertTo-SecureString $env:SYNC_VM_PASSWORD -AsPlainText -Force
$cred = New-Object System.Management.Automation.PSCredential('Suporte', $sec)
$session = New-PSSession -ComputerName 192.168.0.184 -Credential $cred -Authentication Basic

try {
    $stamp = Get-Date -Format yyyyMMddHHmmss
    $remoteTemp = "C:\ProgramData\AraraSuite.com.br\deploy\sync-agent-manual-$stamp"
    $installDir = 'C:\Program Files\AraraSuite.com.br\Sync\Agent'

    Invoke-Command -Session $session -ScriptBlock {
        param($remoteTemp)
        New-Item -ItemType Directory -Path $remoteTemp -Force | Out-Null
    } -ArgumentList $remoteTemp

    # Raiz (dlls/exe/json/pdb) + a pasta runtimes\win\lib\net8.0 (ver nota acima).
    Copy-Item -Path (Join-Path $publishDir '*.dll'), (Join-Path $publishDir '*.exe'), (Join-Path $publishDir '*.json'), (Join-Path $publishDir '*.pdb') -Destination $remoteTemp -ToSession $session -Force
    Invoke-Command -Session $session -ScriptBlock { param($remoteTemp) New-Item -ItemType Directory -Path (Join-Path $remoteTemp 'runtimes\win\lib\net8.0') -Force | Out-Null } -ArgumentList $remoteTemp
    Copy-Item -Path (Join-Path $publishDir 'runtimes\win\lib\net8.0\*.dll') -Destination (Join-Path $remoteTemp 'runtimes\win\lib\net8.0') -ToSession $session -Force

    Invoke-Command -Session $session -ScriptBlock {
        param($remoteTemp, $installDir)
        $ErrorActionPreference = 'Stop'
        $serviceName = 'AraraSuiteSync'
        $backupDir = "C:\ProgramData\AraraSuite.com.br\backups\SyncAgent-bin-$(Get-Date -Format yyyyMMdd-HHmmss)"

        Stop-Service $serviceName -Force
        New-Item -ItemType Directory -Path $backupDir -Force | Out-Null
        Get-ChildItem -LiteralPath $installDir -File | Copy-Item -Destination $backupDir -Force

        Get-ChildItem -LiteralPath $remoteTemp -File | Where-Object { $_.Name -ne 'appsettings.json' -and $_.Name -ne 'appsettings.Development.json' } | ForEach-Object {
            Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $installDir $_.Name) -Force
        }

        $runtimesTarget = Join-Path $installDir 'runtimes\win\lib\net8.0'
        New-Item -ItemType Directory -Path $runtimesTarget -Force | Out-Null
        Get-ChildItem -LiteralPath (Join-Path $remoteTemp 'runtimes\win\lib\net8.0') -File | Copy-Item -Destination $runtimesTarget -Force

        Start-Service $serviceName
        Start-Sleep -Seconds 5

        [pscustomobject]@{
            ServiceStatus = (Get-Service $serviceName).Status.ToString()
            BackupDir = $backupDir
            DashboardHttp = (Invoke-WebRequest -Uri 'http://127.0.0.1:47891/' -UseBasicParsing -TimeoutSec 10).StatusCode
            StatusHttp = (Invoke-WebRequest -Uri 'http://127.0.0.1:47891/status' -UseBasicParsing -TimeoutSec 10).StatusCode
        }
    } -ArgumentList $remoteTemp, $installDir
}
finally {
    if ($session) {
        Remove-PSSession $session
    }
}
```

## Instalador e homologacao limpa

Scripts principais:

| Script | Uso |
| --- | --- |
| `infra\windows\build-sync-agent-homologation-bundle.ps1` | Monta pacote de homologacao com payload. |
| `infra\windows\invoke-sync-agent-clean-install-homologation.ps1` | Executa fluxo de instalacao limpa. |
| `infra\windows\test-sync-agent-clean-install.ps1` | Valida servico, dashboard e banco local. |
| `infra\windows\uninstall-sync-agent.ps1` | Remove instalacao do SyncAgent. |

Fluxo recomendado:

1. Gerar pacote de homologacao.
2. Copiar a pasta completa do pacote para a VM, nao apenas o `.exe`.
3. Executar o instalador como Administrador.
4. Validar servico, dashboard, PostgreSQL e pgvector.
5. Guardar evidencias em `artifacts`.

## Homologacao da UI do PDV App (refactor 2026-07)

Suite UIAutomation do novo fluxo (login -> caixa -> venda -> pagamento):
`tests\homologation\pdv-ui-refactor-2026-07-test.ps1`.

Pre-requisitos na VM:

1. PDV App v1.1.0+ instalado em `C:\Program Files\AraraSuite.com.br\PDV`
   (nome exato da subpasta nao reconfirmado nesta VM apos a migracao de
   nomenclatura — valide com `Get-ChildItem` antes de assumir; via
   auto-update do SyncAgent ou copia manual preservando `appsettings.json`).
2. Operador de teste com senha conhecida. O script usa `admin` /
   `Homolog@2026` por padrao (parametros `-OperatorLogin` / `-OperatorPassword`).
   Para definir o hash no banco local da VM, gere com o Django do ERP:

   ```powershell
   D:\GitHub\erp\venv\Scripts\python.exe -c "from django.contrib.auth.hashers import make_password; print(make_password('Homolog@2026'))"
   ```

   e aplique via psql na VM:

   ```sql
   UPDATE pdv.operators SET password_hash = '<hash>' WHERE login = 'admin';
   ```

   Atencao: o proximo snapshot de operadores do ERP pode sobrescrever o hash.
3. Executar em sessao interativa via `schtasks /Create ... /IT /RU Suporte` e
   `/Run`; coletar `C:\ProgramData\AraraSuite.com.br\Homologation\ui-refactor-2026-07\results.json`.

### Deploy do PDV App v1.1.0 via auto-update (sem WinRM)

1. `infra\windows\build-sync-agent-package.ps1 -Version 1.1.0` gera o pacote e
   registra no ERP integrado (`register_sync_package`).
2. Servir `artifacts\sync-agent-installer` na porta 8099 da maquina dev:

   ```powershell
   cd D:\GitHub\pdv-local\artifacts\sync-agent-installer
   python -m http.server 8099 --bind 0.0.0.0
   ```

   Liberar a porta no firewall (PowerShell como Administrador):

   ```powershell
   New-NetFirewallRule -DisplayName "PDV Local Package 8099" -Direction Inbound -Action Allow -Protocol TCP -LocalPort 8099
   ```

3. O SyncAgent da VM detecta a versao nova no proximo heartbeat e se atualiza
   (SyncAgent + PDVApp), preservando `appsettings.json`.

## Regras de seguranca do Arpa

- O SyncAgent nunca grava no banco Arpa.
- A conexao Arpa deve usar usuario read-only.
- O usuario read-only pode ser criado pela ferramenta do instalador usando o
  usuario master padrao do Arpa, quando aplicavel.
- O banco Arpa do fabricante usa `postgres` sem senha em muitos ambientes; isso
  nao deve virar justificativa para o agente operar com permissao de escrita.
- Views/grants pendentes devem ser gerados a partir do contrato em
  `infra\arpa\sync-export-views-contract.sql`.

## Checklist antes de finalizar uma alteracao

Para SyncAgent:

1. `dotnet build D:\GitHub\pdv-local\src\sync-agent\SyncAgent.csproj`
2. Publicar se a VM precisa ser atualizada.
3. Validar `Get-Service 'AraraSuiteSync'`.
4. Validar `http://127.0.0.1:47891/` e `/status` dentro da VM.
5. Confirmar que `appsettings.json` foi preservado ou alterado com backup.

Para ERP:

1. `D:\GitHub\erp\venv\Scripts\python.exe D:\GitHub\erp\manage.py check`
2. Rodar testes relevantes com `--keepdb`.
3. Rodar `makemigrations --check --dry-run` se houve mudanca de model.
4. Nao editar dados de plano pelo ERP; o CP e fonte de verdade.

Para contratos:

1. Revisar `D:\GitHub\sync`.
2. Atualizar exemplos oficiais.
3. Aplicar versionamento se houver mudanca breaking.

## Problemas conhecidos

- Usar Python global no ERP pode falhar por dependencia ausente. Use o
  `D:\GitHub\erp\venv\Scripts\python.exe`.
- `manage.py check` mostra warning conhecido do CKEditor 4.
- Testes do ERP podem avisar sobre migrations pendentes em apps fora do escopo;
  nao trate isso como falha do Sync sem confirmar o app afetado.
- A VM so permite automacao remota se WinRM estiver ativo e o usuario
  operacional existir no grupo Administradores.
