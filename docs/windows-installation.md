# Instalacao Windows - Sync Agent

Este documento descreve o caminho alvo para cliente final sem depender de
Docker Desktop.

## Modelo de instalacao

No cliente, a instalacao deve ser composta por:

- PostgreSQL 17 com pgvector 0.8.0.
- Banco local `pdv_sync`.
- Usuario local `pdv_sync`.
- Schema `sync_agent`.
- Schema `pdv`.
- `PDV App` instalado como aplicacao desktop.
- `SyncAgent` instalado como Windows Service.
- `SyncAgent.Tray` iniciado junto com a sessao do usuario.
- Timezone padrao do banco: `America/Sao_Paulo` (Brasilia, UTC-3).

Docker Compose permanece apenas como ambiente de desenvolvimento.

## Pacote de instalacao

Na maquina de build/desenvolvimento, gere o pacote:

```powershell
.\infra\windows\build-sync-agent-package.ps1
```

O pacote fica em:

```text
artifacts\sync-agent-installer
```

Ele contem:

- payload publicado do `SyncAgent`;
- payload publicado do `SyncAgent.Tray`;
- payload publicado do `PDV App`;
- payload do .NET 8 Desktop Runtime para instalacao silenciosa;
- payload do Microsoft Visual C++ Redistributable x64 para dependencias
  nativas do PostgreSQL/`psql.exe`;
- payload do PostgreSQL 17;
- payload do pgvector `0.8.0`;
- scripts de instalacao/desinstalacao;
- script de smoke test operacional;
- scripts SQL de bootstrap do PostgreSQL;
- `self-update.ps1` para atualizacao automatica remota;
- arquivo `VERSION` com a versao instalada;
- guias operacionais em `docs`.

### Pacote versionado para release

Para gerar um pacote com versao especifica (usado pelo auto-update):

```powershell
.\infra\windows\build-sync-agent-package.ps1 -Version 1.1.0
```

Gera adicionalmente:

```text
artifacts\sync-agent-installer\pdv-local-v1.1.0.zip
artifacts\sync-agent-installer\pdv-local-v1.1.0.zip.sha256
```

Para release automatico via GitHub Actions, crie uma tag e faca push:

```powershell
git tag v1.1.0
git push origin v1.1.0
```

O workflow `.github/workflows/release.yml` constroi e publica o ZIP e o
arquivo `.sha256` na aba Releases do repositorio.

## Bootstrap do banco

Com PostgreSQL instalado e `psql` disponivel no `PATH`, execute em PowerShell:

```powershell
.\infra\windows\bootstrap-sync-agent-db.ps1
```

Parametros principais:

```powershell
.\infra\windows\bootstrap-sync-agent-db.ps1 `
  -PostgresHost localhost `
  -PostgresPort 5432 `
  -AdminUser postgres `
  -DatabaseName pdv_sync `
  -DatabaseUser pdv_sync `
  -DatabasePassword pdv_sync
```

O script aplica:

- `infra/postgres/init/00-set-timezone.sql`
- `infra/postgres/init/01-create-vector-extension.sql`
- `infra/postgres/init/02-create-sync-agent-schema.sql`
- `infra/postgres/init/03-create-pdv-schema.sql`

## Instalacao automatizada

Pre-requisitos no cliente:

- PostgreSQL 17 instalado.
- pgvector 0.8.0 disponivel na instalacao do PostgreSQL.
- `psql` disponivel no `PATH` ou caminho informado por `-PsqlPath`.
- .NET 8 Runtime instalado apenas se os payloads do Agent/Tray forem
  publicados como `--self-contained false`.
- O `SyncAgent.Installer.exe` deve ser publicado self-contained para abrir em
  maquina limpa sem .NET Desktop Runtime pre-instalado.

Instalacao completa do agente:

```powershell
.\infra\windows\install-sync-agent.ps1 `
  -InstanceId "local-dev-agent-01" `
  -ErpTenantId "local-dev" `
  -ErpApiBaseUrl "https://erp.exemplo.com" `
  -AccessToken "<token-emitido-pelo-ERP>" `
  -ClientCertificateThumbprint "<thumbprint-certificado-cliente>" `
  -PostgresHost localhost `
  -PostgresPort 5432 `
  -PostgresAdminUser postgres `
  -PostgresAdminPassword (Read-Host "Senha admin PostgreSQL" -AsSecureString) `
  -DatabaseName pdv_sync `
  -DatabaseUser pdv_sync `
  -DatabasePassword "<senha-local-pdv-sync>"
```

O instalador:

- aplica o bootstrap do banco local;
- provisiona token em variavel de ambiente de maquina quando o token for
  informado;
- pode deixar o agente em modo de ativacao pos-instalacao com
  `-EnablePostInstallActivation`;
- importa PFX quando `-PfxPath` for informado;
- opcionalmente protege a senha read-only do Arpa com DPAPI LocalMachine;
- copia os artefatos para `C:\Program Files\PDVLocal`;
- copia o PDV App para `C:\Program Files\PDVLocal\PDVApp`;
- grava `appsettings.json` provisionado do SyncAgent;
- grava `appsettings.json` provisionado do PDV App;
- habilita `ErpPdvSnapshot` no SyncAgent instalado para importar operadores do
  ERP para `pdv.operators`;
- instala/atualiza o Windows Service `PDV Local Sync Agent`;
- configura restart automatico do servico em falha;
- cria atalho do tray na inicializacao do Windows.
- cria atalho `PDV Local.lnk` na area de trabalho publica.
- cria atalho `PDV Local.lnk` no Menu Iniciar.

## Importacao de operadores do ERP

O SyncAgent instalado inclui a secao:

```json
{
    "ErpPdvSnapshot": {
    "Enabled": true,
    "TimeoutSeconds": 30,
    "Limit": 1000
  }
}
```

Quando habilitado, cada ciclo do SyncAgent consulta:

```text
GET /v1/sync/pdv/operators:snapshot
```

Regras:

- o endpoint usa o bearer token do SyncAgent;
- o ERP nao envia senha, hash de senha, token ou segredo;
- o banco local grava `external_operator_id` com o ID original do ERP;
- o `operator_id` local e um UUID deterministico para manter o schema local;
- `password_hash` fica vazio para operador importado nesta fase;
- a ultima execucao aparece na aba Logs do dashboard como
  `pdv_operator_snapshot`.
- se o ERP responder `has_more=true`, o SyncAgent importa o lote recebido, mas
  nao avanca o watermark; a tarefa fica visivel como falha parcial no log para
  evitar perda silenciosa de operadores.

Para desabilitar temporariamente:

```json
{
  "ErpPdvSnapshot": {
    "Enabled": false
  }
}
```

Para instalar ja com o coletor Arpa habilitado no piloto Anapolis:

```powershell
.\infra\windows\install-sync-agent.ps1 `
  -InstanceId "anapolis-local-test-01" `
  -ErpTenantId "piloto-anapolis" `
  -ErpApiBaseUrl "https://erp.exemplo.com" `
  -AccessToken "<token-emitido-pelo-ERP>" `
  -ClientCertificateThumbprint "<thumbprint-certificado-cliente>" `
  -PostgresAdminPassword (Read-Host "Senha admin PostgreSQL" -AsSecureString) `
  -DatabasePassword "<senha-local-pdv-sync>" `
  -EnableArpaCollector `
  -ArpaConnectionString "Host=192.168.0.4;Port=5432;Database=anapolis;Username=sync_agent_anapolis_ro" `
  -ArpaPassword (Read-Host "Senha read-only Arpa" -AsSecureString) `
  -ArpaCollectorPreset AnapolisInitialLoad `
  -ArpaBatchSize 5000
```

Para instalar sem credenciais do ERP e ativar pelo dashboard local depois:

```powershell
.\infra\windows\install-sync-agent.ps1 `
  -EnablePostInstallActivation `
  -PostgresAdminPassword (Read-Host "Senha admin PostgreSQL" -AsSecureString) `
  -DatabasePassword "<senha-local-pdv-sync>"
```

Esse modo grava:

```json
{
  "Provisioning": {
    "Enabled": true,
    "ProtectedFile": "C:\\Program Files\\PDVLocal\\Secrets\\sync-agent-provisioning.dpapi",
    "ActivationTimeoutSeconds": 30
  }
}
```

Apos instalar, abrir:

```text
http://127.0.0.1:47891/setup
```

O cliente informa a URL do ERP e o codigo de ativacao. O SyncAgent salva as
credenciais tecnicas com DPAPI `LocalMachine` e nao armazena usuario/senha do
ERP. Enquanto nao ativar, o servico permanece `not_provisioned` e nao executa
coleta, envio, heartbeat remoto ou reconciliacao remota.

No modo com `-EnableArpaCollector`, o instalador grava a senha do Arpa em
arquivo protegido por DPAPI:

```text
C:\Program Files\PDVLocal\Secrets\arpa-runtime-password.dpapi
```

O `appsettings.json` do servico aponta para esse arquivo em
`ArpaCollector:PasswordProtectedFile`. A senha nao e gravada em texto puro no
arquivo de configuracao.

Para validar o pacote sem exigir Administrador e sem criar servico:

```powershell
.\artifacts\sync-agent-installer\infra\install-sync-agent.ps1 -ValidateOnly
```

Esse modo confere se o pacote possui payload do `SyncAgent`, payload do tray e
scripts essenciais. Ele nao copia arquivos, nao cria servico, nao altera banco
e nao grava segredos.

## Instalador interativo

O pacote inclui um instalador interativo com fluxo de avancar etapas:

```text
payload\SyncAgentInstaller\SyncAgent.Installer.exe
```

Esse executavel e publicado self-contained (`win-x64`) para nao exigir .NET
Desktop Runtime na maquina limpa antes da instalacao.

Ele coleta:

- dados do PostgreSQL local;
- senha admin do PostgreSQL;
- senha do usuario local `pdv_sync`;
- opcionalmente, dados do coletor Arpa read-only em campos separados:
  - Host;
  - Porta;
  - Database;
  - Username;
  - senha read-only em campo protegido.

Defaults atuais do coletor Arpa no instalador:

- Host: `127.0.0.1`;
- Porta: `5432`;
- Database: `control`;
- Username: `sync_agent_anapolis_ro`;
- Senha read-only: `postgres`.

O instalador interativo monta internamente a connection string do Arpa sem senha,
no formato `Host=<host>;Port=<porta>;Database=<database>;Username=<username>`.
A senha continua separada e protegida por DPAPI.

Na etapa do coletor Arpa, o botao `Testar conexao` valida a conexao antes da
instalacao usando `psql` e a senha informada em memoria. O teste executa apenas
consulta de leitura (`SELECT current_database(), current_user`) para confirmar
host, porta, database, usuario e senha. Ele nao cria objetos, nao altera dados e
nao grava nada no banco Arpa.

O botao `Preparar views` e o fluxo recomendado quando a base Arpa ainda nao
possui `sync_export.produtos` e `sync_export.clientes`. Ele conecta como
`postgres` sem senha, aplica o SQL aprovado
`infra\arpa\sync-export-views-anapolis.initial-load.sql`, cria/ajusta o usuario
read-only informado, aplica os grants nas views e executa o teste de conexao ao
final. Essa acao cria apenas views de exportacao e usuario/permissoes tecnicas;
nao grava dados de negocio no Arpa.

Quando o usuario read-only ainda nao existir no Arpa, o botao `Criar usuario`
executa uma preparacao administrativa explicita conectando como `postgres` sem
senha, conforme padrao do fabricante informado para o Arpa. Essa acao cria ou
altera somente o usuario informado, define a senha digitada no campo protegido,
concede `CONNECT` no database e revoga permissoes de escrita no schema
`public`. Se as views `sync_export.produtos` e `sync_export.clientes` ja
existirem, tambem concede `USAGE` no schema `sync_export`, `SELECT` nas views e
revoga permissoes de escrita nelas. Se as views ainda nao existirem, o usuario
e criado mesmo assim e os grants das views ficam pendentes para a etapa que cria
as views.

Essa preparacao nao e fluxo runtime do SyncAgent. O SyncAgent continua usando
somente o usuario read-only e continua proibido de gravar dados no Arpa.
- opcionalmente, executa a validacao de instalacao ao concluir e gera
  `sync-agent-clean-install-evidence.json`.
- instala silenciosamente o .NET 8 Desktop Runtime quando ele ainda nao existir.
- instala silenciosamente o Microsoft Visual C++ Redistributable x64 quando
  necessario para executar `psql.exe` em maquina Windows limpa.

Para homologacao, o instalador ja abre com senhas padrao preenchidas:

- senha admin PostgreSQL: `postgres`;
- senha do usuario local `pdv_sync`: `pdv_sync`.

Esses defaults reduzem atrito na VM limpa e no piloto tecnico. Antes de release
para cliente final, a politica de credenciais de producao deve substituir esses
valores por senha gerada ou definida no provisionamento.

O instalador interativo chama `infra\install-sync-agent.ps1` internamente,
deixando o SyncAgent em modo de ativacao pos-instalacao. A ativacao do ERP
continua sendo feita no dashboard local:

```text
http://127.0.0.1:47891/setup
```

Executar o instalador interativo como Administrador:

```powershell
.\payload\SyncAgentInstaller\SyncAgent.Installer.exe
```

O caminho PowerShell permanece disponivel para automacao e suporte.

Na tela de revisao, a opcao `Executar validacao de instalacao ao concluir`
fica marcada por padrao. Quando habilitada, o instalador chama
`infra\test-sync-agent-clean-install.ps1 -SkipErp` apos instalar o servico e
grava a evidencia no caminho informado.

### Requisito rigido: PostgreSQL no instalador

Estado atual em 02/06/2026:

- o instalador interativo possui etapa PostgreSQL;
- no modo "usar PostgreSQL 17 ja instalado", ele valida `psql.exe`,
  conexao admin e disponibilidade de pgvector `0.8.0` antes de avancar;
- no modo "instalar PostgreSQL 17 automaticamente", ele executa
  `infra\install-postgresql17-local.ps1` antes do SyncAgent;
- a instalacao automatica usa binarios oficiais PostgreSQL 17 empacotados em
  `payload\PostgreSQL17\pgsql`;
- os binarios oficiais PostgreSQL 17.10 baixados nao incluem pgvector;
- a documentacao oficial do pgvector 0.8.0 registra compilacao Windows com
  PostgreSQL 16 e observacao de limitacao para PostgreSQL 17;
- por isso, a instalacao continua bloqueada se pgvector `0.8.0` nao estiver
  disponivel no PostgreSQL final.

Proxima execucao obrigatoria:

- definir e empacotar a origem aprovada do pgvector `0.8.0` para Windows;
- validar a instalacao automatica em maquina Windows limpa;
- regenerar o pacote/bundle depois de fechar qualquer processo aberto do
  instalador.

Fluxo alvo da etapa "PostgreSQL":

```text
PostgreSQL
[ ] Usar PostgreSQL 17 ja instalado
[ ] Instalar PostgreSQL 17 automaticamente

Host: localhost
Porta: 5432
Usuario admin: postgres
Senha admin: ********
Caminho psql.exe: <detectado ou informado>
```

Se "Usar PostgreSQL 17 ja instalado" for escolhido:

- validar `psql.exe`;
- validar versao major `17`;
- validar conexao admin;
- validar/instalar extensao `vector`;
- validar `SELECT extversion FROM pg_extension WHERE extname = 'vector'`;
- aceitar apenas `0.8.0` como versao alvo desta fase.

Se "Instalar PostgreSQL 17 automaticamente" for escolhido:

- usar instalador offline ou binarios previamente empacotados/aprovados;
- executar instalacao silenciosa como Administrador;
- configurar servico local;
- configurar porta;
- configurar senha do usuario `postgres`;
- garantir que `psql.exe` fique localizavel pelo instalador;
- instalar ou disponibilizar pgvector 0.8.0;
- executar as mesmas validacoes do modo existente.

Implementacao atual da instalacao automatica:

- script: `infra\windows\install-postgresql17-local.ps1`;
- instala em `C:\Program Files\PostgreSQL\17` por padrao;
- inicializa dados em `C:\ProgramData\PDVLocal\PostgreSQL17\data` por padrao;
- registra servico `postgresql-x64-17-pdvlocal`;
- usa arquivo temporario para passar a senha ao `initdb`, removendo o arquivo
  ao final;
- valida PostgreSQL major `17`;
- copia payload pgvector quando existir em `payload\PostgreSQL17\pgvector`;
- valida pgvector `0.8.0` antes de permitir a instalacao do SyncAgent.

Formato esperado do payload pgvector aprovado:

```text
payload\PostgreSQL17\pgvector\lib\vector.dll
payload\PostgreSQL17\pgvector\share\extension\vector.control
payload\PostgreSQL17\pgvector\share\extension\vector--0.8.0.sql
```

O mesmo formato pode ser preparado no repositorio antes do build em:

```text
artifacts\postgresql-17\pgvector
```

Gate de origem aprovada:

- nao usar binario aleatorio de terceiros baixado da internet;
- aceitar somente build interno reprodutivel ou fornecedor oficial/suporte contratado;
- registrar SHA256 de `vector.dll`, `vector.control` e `vector--0.8.0.sql`;
- fonte primaria aceita para build interno: repositorio oficial
  `https://github.com/pgvector/pgvector.git`, tag `v0.8.0`;
- tag `v0.8.0` preparada localmente em 02/06/2026 no commit
  `2627c5ff775ae6d7aef0c430121ccf857842d2f2`;
- no Windows, o build deve usar Visual Studio C++ Build Tools e `nmake`
  conforme instrucao oficial do pgvector;
- PostgreSQL 17 precisa ser 17.3+ para evitar falha de link conhecida no
  Windows; o pacote atual usa PostgreSQL 17.10;
- validar o payload antes do pacote com:

```powershell
.\infra\windows\test-postgresql17-pgvector-payload.ps1 `
  -PayloadRoot .\artifacts\postgresql-17\pgvector `
  -PgVectorVersion 0.8.0
```

O empacotador tambem executa esse preflight quando a pasta
`artifacts\postgresql-17\pgvector` existir. Se o payload estiver incompleto ou
com versao diferente, o pacote falha.

Para gerar o payload por build interno reprodutivel:

```powershell
.\infra\windows\build-postgresql17-pgvector-payload.ps1 `
  -PgVectorVersion 0.8.0 `
  -PgRoot .\artifacts\postgresql-17\extracted\pgsql `
  -OutputRoot .\artifacts\postgresql-17\pgvector
```

Pre-requisitos desse build:

- Git disponivel no `PATH`;
- Visual Studio 2022 Build Tools com componente C++ x64;
- `vcvars64.bat` detectavel ou informado por `-VcVars64Path`;
- PostgreSQL 17.3+ com headers em `PgRoot`.

Para preparar apenas a fonte oficial sem compilar:

```powershell
.\infra\windows\build-postgresql17-pgvector-payload.ps1 -PrepareSourceOnly
```

Payload aprovado gerado em 02/06/2026 contra PostgreSQL 17.10:

```text
vector.dll sha256:     5796de96b333a83ee42a58c96bf89aca35c217b2acb8433a2836a340eb5ead30
vector.control sha256: fe92db63f7fb3f574830204dca304a0fe07da9f1bf9b179768e729b50a906c80
vector--0.8.0.sql:     1e4d2de57f0a16c5c2b259d77655aecf9c740713beae4ac4b511549fceb6aafb
```

Criterios de aceite:

- instalacao nova em Windows limpo sem Docker Desktop;
- PostgreSQL major `17`;
- pgvector `0.8.0`;
- timezone do banco `America/Sao_Paulo`;
- banco `pdv_sync` criado;
- usuario `pdv_sync` criado;
- schema `sync_agent` criado;
- schema `pdv` criado;
- SyncAgent instalado somente depois das validacoes acima;
- erro claro na tela se qualquer validacao falhar;
- nenhuma senha gravada em texto puro em arquivo de configuracao.

NO-GO:

- continuar instalacao do SyncAgent sem PostgreSQL 17 validado;
- continuar instalacao do SyncAgent sem pgvector 0.8.0 validado;
- depender de Docker Desktop;
- aceitar PostgreSQL de major diferente;
- deixar senha admin PostgreSQL em log, argumento de processo ou
  `appsettings.json`.

Para usar artefatos ja publicados sem recompilar:

```powershell
.\infra\windows\install-sync-agent.ps1 `
  -InstanceId "local-dev-agent-01" `
  -ErpTenantId "local-dev" `
  -ErpApiBaseUrl "https://erp.exemplo.com" `
  -SkipServiceStart
```

Para ambiente em que o banco ja foi preparado:

```powershell
.\infra\windows\install-sync-agent.ps1 `
  -InstanceId "local-dev-agent-01" `
  -ErpTenantId "local-dev" `
  -ErpApiBaseUrl "https://erp.exemplo.com" `
  -SkipDatabaseBootstrap
```

## Validacao

```powershell
psql -U pdv_sync -d pdv_sync -c "SELECT extname, extversion FROM pg_extension WHERE extname = 'vector';"
psql -U pdv_sync -d pdv_sync -c "SHOW timezone;"
psql -U pdv_sync -d pdv_sync -c "SELECT table_name FROM information_schema.tables WHERE table_schema = 'sync_agent' ORDER BY table_name;"
psql -U pdv_sync -d pdv_sync -c "SELECT table_name FROM information_schema.tables WHERE table_schema = 'pdv' ORDER BY table_name;"
dotnet build pdv-local.sln
dotnet run --project src/sync-agent/SyncAgent.csproj
```

Apos instalar como servico:

```powershell
Get-Service "PDV Local Sync Agent"
Invoke-RestMethod -Uri "http://127.0.0.1:47891/status" -Method Get
```

Smoke test local:

```powershell
.\infra\windows\test-sync-agent-smoke.ps1 -SkipErp
```

Smoke test com ERP:

```powershell
.\infra\windows\test-sync-agent-smoke.ps1 `
  -ErpApiBaseUrl "https://erp.exemplo.com" `
  -InstanceId "local-dev-agent-01" `
  -AccessToken "<token-emitido-pelo-ERP>"
```

Readiness de homologacao/release:

```powershell
.\infra\windows\test-sync-agent-release-readiness.ps1 `
  -ErpApiBaseUrl "https://erp.exemplo.com" `
  -InstanceId "local-dev-agent-01" `
  -AccessTokenFile "C:\Secrets\sync-agent.token" `
  -EvidenceOutput ".\artifacts\sync-agent-release-readiness.json"
```

Esse comando valida pacote, contrato, dashboard local, logs, sinal de sync,
fila, dead-letter, headers locais e status central no ERP. O arquivo de
evidencia nao inclui o token.

Homologacao de instalacao limpa em VM/maquina nova:

```powershell
.\infra\invoke-sync-agent-clean-install-homologation.ps1 `
  -PostgresAdminPassword (Read-Host "Senha admin PostgreSQL" -AsSecureString) `
  -DatabasePassword "<senha-local-pdv-sync>" `
  -SkipErp `
  -EvidenceOutput ".\artifacts\sync-agent-clean-install-evidence.json"
```

Esse script instala PostgreSQL 17 + pgvector `0.8.0`, instala o SyncAgent em
modo de ativacao pos-instalacao e executa a validacao de instalacao limpa.

Se a maquina limpa exibir erro de `VCRUNTIME140.dll` ao executar `psql.exe`,
usar o bundle atualizado: ele inclui `payload\VcRuntime\vc_redist.x64.exe` e o
instalador executa essa dependencia antes de validar PostgreSQL ou Arpa.

Para piloto tecnico, seguir tambem:

```text
docs/sync-agent-piloto-readiness.md
```

## Producao

Em producao, as credenciais nao devem ficar no `appsettings.json`. A connection
string e a identidade do agente devem vir do provisionamento do ERP/Control
Plane, usando variaveis de ambiente, secret manager ou cofre local do Windows.

O dispatcher ERP aceita Bearer token e certificado cliente por configuracao,
mas esses valores devem ser injetados por mecanismo protegido do Windows no
instalador final. Nao registrar token, senha de certificado ou payload de
cliente em log operacional.

Bootstrap inicial de seguranca:

```powershell
.\infra\windows\provision-sync-agent-security.ps1 `
  -AccessToken "<token-curto-ou-token-inicial>" `
  -PfxPath "C:\Install\sync-agent-client.pfx" `
  -PfxPassword (Read-Host "Senha do PFX" -AsSecureString)
```

O token e salvo como variavel de ambiente de maquina
`PDV_SYNC_ERP_ACCESS_TOKEN`. O certificado e importado no Windows Certificate
Store sem marcar a chave privada como exportavel. Depois configure
`ErpSecurity:ClientCertificateThumbprint` com o thumbprint exibido pelo script.

Os campos de contrato com sufixo `_utc` devem continuar sendo enviados como UTC.
A timezone `America/Sao_Paulo` e o padrao local do PostgreSQL para sessao,
exibicao e defaults como `now()`.

## Windows Service

O Worker ja esta preparado para rodar como Windows Service.

Publicacao exemplo:

```powershell
dotnet publish src/sync-agent/SyncAgent.csproj `
  -c Release `
  -r win-x64 `
  --self-contained false `
  -o C:\Program Files\PDVLocal\SyncAgent
```

Instalacao manual do servico:

```powershell
sc.exe create "PDV Local Sync Agent" `
  binPath= "\"C:\Program Files\PDVLocal\SyncAgent\SyncAgent.exe\"" `
  start= auto

sc.exe start "PDV Local Sync Agent"
```

Remocao manual:

```powershell
sc.exe stop "PDV Local Sync Agent"
sc.exe delete "PDV Local Sync Agent"
```

## App de bandeja

O projeto `src/sync-agent-tray/SyncAgent.Tray.csproj` cria o icone no relogio
do Windows. Ele consulta a API local do servico em `http://127.0.0.1:47891`.

Publicacao exemplo:

```powershell
dotnet publish src/sync-agent-tray/SyncAgent.Tray.csproj `
  -c Release `
  -r win-x64 `
  --self-contained false `
  -o C:\Program Files\PDVLocal\SyncAgentTray
```

O app de bandeja deve iniciar na sessao do usuario, enquanto o `SyncAgent`
permanece como servico de sistema.

## Desinstalacao

Por padrao, a desinstalacao remove o servico e preserva banco/arquivos:

```powershell
.\infra\windows\uninstall-sync-agent.ps1
```

Para remover tambem o atalho do tray:

```powershell
.\infra\windows\uninstall-sync-agent.ps1 -RemoveTrayStartup
```

Para remover arquivos instalados em `C:\Program Files\PDVLocal`:

```powershell
.\infra\windows\uninstall-sync-agent.ps1 -RemoveTrayStartup -RemoveFiles
```

O banco local nao e removido automaticamente para preservar outbox,
dead-letter, auditoria e evidencias de sincronizacao.


