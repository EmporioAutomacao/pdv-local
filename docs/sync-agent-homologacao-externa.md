# Sync Agent - Fase 2 Homologacao Externa

Data: 02/06/2026

## Objetivo

Validar o SyncAgent fora do ambiente de desenvolvimento, em maquina Windows
limpa ou equivalente, usando instalacao real, servico Windows, banco local sem
Docker, ativacao pos-instalacao e evidencias operacionais.

Esta fase parte da verificacao rigida concluida em:

```text
docs/sync-agent-verificacao-rigida-2026-06-02.md
```

## Escopo

Incluido:

- instalacao pelo pacote `artifacts/sync-agent-installer`;
- execucao do instalador como Administrador;
- .NET 8 Desktop Runtime instalado silenciosamente pelo pacote;
- Microsoft Visual C++ Redistributable x64 instalado silenciosamente pelo pacote
  para permitir uso do PostgreSQL/`psql.exe` em maquina limpa;
- PostgreSQL 17 local com pgvector 0.8.0;
- banco local `pdv_sync`;
- timezone `America/Sao_Paulo`;
- Windows Service `PDV Local Sync Agent`;
- Tray na inicializacao do usuario;
- ativacao pelo dashboard local `/setup`;
- validacao de reinicio do servico e do Windows;
- validacao de dashboard, logs, fila, dead-letter e reconciliacao;
- validacao do rollback/desinstalacao preservando banco local.

Fora do escopo tecnico desta fase:

- escrita no banco Arpa;
- fluxo `ERP -> SyncAgent -> Arpa`;
- operacao multi-tenant em uma mesma instalacao;
- assinatura final do EXE, enquanto o canal definitivo ainda nao estiver
  definido;
- mTLS real quando o gateway/certificado final ainda nao estiver disponivel.

## Pre-requisitos

Na maquina alvo:

- Windows com acesso de Administrador;
- PostgreSQL 17 instalado previamente ou instalavel pelo instalador
  interativo;
- pgvector 0.8.0 instalado previamente ou instalavel pelo instalador
  interativo;
- `psql` no `PATH`, caminho conhecido ou detectavel pelo instalador;
- .NET 8 Runtime apenas se os payloads do Agent/Tray forem publicados como
  `--self-contained false`;
- `SyncAgent.Installer.exe` self-contained, sem exigir .NET Desktop Runtime
  pre-instalado;
- conectividade com ERP de homologacao;
- pacote `sync-agent-installer` copiado localmente;
- senha do usuario local `pdv_sync` definida;
- se houver Arpa real, usuario runtime read-only validado;
- codigo de ativacao gerado no ERP para o cliente/tenant correto.

No ERP de homologacao:

- migrations do `sync_api` aplicadas;
- contrato `../sync` compativel com `1.4.0`;
- endpoints `/v1/sync/*` acessiveis;
- emissao de codigo de ativacao disponivel;
- se mTLS estiver ativo, certificado cliente emitido/instalavel.

## Ordem de execucao

### 1. Gerar bundle de homologacao

Na maquina de desenvolvimento/build:

```powershell
.\infra\windows\build-sync-agent-homologation-bundle.ps1
```

Esse comando gera:

- `artifacts/homologation/<bundle>.zip`;
- `artifacts/homologation/<bundle>.manifest.json`;
- `artifacts/homologation/<bundle>.sha256.txt`.

Copiar o `.zip` para a maquina alvo e conferir o SHA256 antes de instalar.

O pacote tambem inclui o instalador interativo:

```text
payload\SyncAgentInstaller\SyncAgent.Installer.exe
```

Esse EXE e o caminho recomendado para homologacao com usuario final. Ele
coleta os dados necessarios em telas "Avancar/Voltar", executa o instalador
PowerShell interno e deixa a ativacao do ERP para o dashboard local `/setup`.

O caminho PowerShell continua existindo para automacao, suporte tecnico e
ambientes onde a instalacao precise ser reproduzida por script.

### 2. Validar pacote antes de instalar

Na pasta do pacote:

```powershell
.\infra\install-sync-agent.ps1 -ValidateOnly
```

Resultado esperado:

- payload do SyncAgent presente;
- payload do Tray presente;
- scripts essenciais presentes.

### 3. Instalar em modo ativacao pos-instalacao

Opcao recomendada, com interface:

```powershell
.\payload\SyncAgentInstaller\SyncAgent.Installer.exe
```

Executar como Administrador e informar:

- caminho de instalacao;
- escolha da etapa PostgreSQL:
  - usar PostgreSQL 17 existente;
  - instalar PostgreSQL 17 automaticamente;
- caminho do `psql.exe`, se a deteccao automatica nao localizar;
- host/porta/usuario/senha admin do PostgreSQL 17 local;
- banco e usuario local `pdv_sync`;
- opcionalmente, conexao read-only do Arpa em campos separados:
  - Host;
  - Porta;
  - Database;
  - Username;
  - senha read-only.

Defaults atuais do coletor Arpa:

- Host: `127.0.0.1`;
- Porta: `5432`;
- Database: `control`;
- Username: `sync_agent_anapolis_ro`;
- senha read-only: `postgres`.

Quando o coletor Arpa estiver habilitado, usar o botao `Testar conexao` antes
de avancar. O teste usa `psql` e executa somente uma consulta de leitura para
confirmar conectividade e credenciais. Ele nao grava dados, nao cria views e nao
altera permissoes no banco Arpa.

Fluxo recomendado quando a base ainda nao tiver as views:

1. Clicar em `Preparar views`.
2. O instalador aplica `infra\arpa\sync-export-views-anapolis.initial-load.sql`
   como `postgres` sem senha.
3. O instalador cria/ajusta o usuario read-only informado.
4. O instalador aplica os grants nas views.
5. O instalador executa o teste de conexao com o usuario read-only.

Esse fluxo cria views tecnicas de exportacao e usuario/permissoes, mas nao
grava dados de negocio no Arpa.

Se o usuario read-only ainda nao existir, usar o botao `Criar usuario`. Essa
acao e administrativa e deve ser executada conscientemente pelo instalador: ela
conecta como `postgres` sem senha, cria/altera o usuario informado, concede
`CONNECT` no database e revoga permissoes de escrita no schema `public`. Se as
views `sync_export.produtos` e `sync_export.clientes` existirem, aplica tambem
os grants read-only nelas. Se as views ainda nao existirem, o usuario e criado e
os grants das views ficam pendentes para a etapa de criacao das views.

Isso nao altera a regra de seguranca do runtime: o SyncAgent nunca grava dados
no Arpa e opera apenas com o usuario read-only.

Para esta homologacao, o instalador interativo ja vem com as senhas padrao
preenchidas:

- senha admin PostgreSQL: `postgres`;
- senha do usuario local `pdv_sync`: `pdv_sync`.

Esses valores sao defaults de homologacao/piloto tecnico. A release final deve
trocar essa politica por credencial gerada, definida no provisionamento ou
controlada por politica operacional aprovada.

O instalador interativo sempre usa `-EnablePostInstallActivation`. Isso
significa que ele instala o servico, prepara o banco local e abre o dashboard
para ativacao do ERP depois da instalacao.

Na tela de revisao, manter marcada a opcao:

```text
Executar validacao de instalacao ao concluir
```

Resultado esperado:

- o instalador executa `test-sync-agent-clean-install.ps1 -SkipErp`;
- o arquivo `sync-agent-clean-install-evidence.json` e gerado no caminho
  informado;
- se a validacao falhar, o instalador mostra erro e nao conclui a homologacao.

Estado atual em 02/06/2026: o instalador interativo ja possui etapa
PostgreSQL e pode executar instalacao automatica a partir de
`payload\PostgreSQL17\pgsql`. A homologacao completa desta etapa so podera ser
marcada como GO depois que a origem aprovada do pgvector `0.8.0` para Windows
for empacotada e validada, porque os binarios oficiais PostgreSQL 17.10 nao
incluem essa extensao.

Formato esperado no pacote:

```text
payload\PostgreSQL17\pgvector\lib\vector.dll
payload\PostgreSQL17\pgvector\share\extension\vector.control
payload\PostgreSQL17\pgvector\share\extension\vector--0.8.0.sql
```

Antes de gerar o bundle final, validar o payload aprovado:

```powershell
.\infra\windows\test-postgresql17-pgvector-payload.ps1 `
  -PayloadRoot .\artifacts\postgresql-17\pgvector `
  -PgVectorVersion 0.8.0
```

Nao e permitido usar binario pgvector de origem avulsa/nao auditada. A origem
aceita precisa ser build interno reprodutivel ou fornecedor oficial/suporte
contratado, com SHA256 anexado a evidencia da homologacao.

Para build interno reprodutivel, usar:

```powershell
.\infra\windows\build-postgresql17-pgvector-payload.ps1 `
  -PgVectorVersion 0.8.0 `
  -PgRoot .\artifacts\postgresql-17\extracted\pgsql `
  -OutputRoot .\artifacts\postgresql-17\pgvector
```

Esse script usa o repositorio oficial `pgvector/pgvector`, tag `v0.8.0`,
Visual Studio C++ Build Tools e `nmake`. O PostgreSQL alvo deve ser 17.3+;
o pacote atual usa PostgreSQL 17.10.

Fonte oficial preparada em 02/06/2026:

```text
tag: v0.8.0
commit: 2627c5ff775ae6d7aef0c430121ccf857842d2f2
```

Payload aprovado gerado em 02/06/2026 contra PostgreSQL 17.10:

```text
vector.dll sha256:     5796de96b333a83ee42a58c96bf89aca35c217b2acb8433a2836a340eb5ead30
vector.control sha256: fe92db63f7fb3f574830204dca304a0fe07da9f1bf9b179768e729b50a906c80
vector--0.8.0.sql:     1e4d2de57f0a16c5c2b259d77655aecf9c740713beae4ac4b511549fceb6aafb
```

Validacoes obrigatorias antes do SyncAgent ser instalado:

- PostgreSQL major `17`;
- `psql.exe` detectado;
- servico PostgreSQL acessivel;
- payload pgvector aprovado presente, quando a instalacao automatica for usada;
- pgvector `0.8.0`;
- timezone `America/Sao_Paulo`;
- conexao admin valida;
- banco `pdv_sync` criado/preparado sem Docker Desktop.
- SyncAgent instalado somente depois das validacoes acima.

Opcao alternativa para automacao/suporte:

Executar PowerShell como Administrador:

```powershell
.\infra\install-sync-agent.ps1 `
  -EnablePostInstallActivation `
  -PostgresAdminPassword (Read-Host "Senha admin PostgreSQL" -AsSecureString) `
  -DatabasePassword "<senha-local-pdv-sync>"
```

Se o coletor Arpa tambem for habilitado na homologacao:

```powershell
.\infra\install-sync-agent.ps1 `
  -EnablePostInstallActivation `
  -PostgresAdminPassword (Read-Host "Senha admin PostgreSQL" -AsSecureString) `
  -DatabasePassword "<senha-local-pdv-sync>" `
  -EnableArpaCollector `
  -ArpaConnectionString "Host=<host-arpa>;Port=5432;Database=<db-arpa>;Username=<usuario-read-only>" `
  -ArpaPassword (Read-Host "Senha read-only Arpa" -AsSecureString) `
  -ArpaCollectorPreset AnapolisInitialLoad `
  -ArpaBatchSize 5000
```

Resultado esperado:

- Microsoft Visual C++ Runtime instalado ou ja existente;
- .NET 8 Desktop Runtime instalado ou ja existente;
- servico Windows instalado;
- appsettings gravado sem token ERP;
- `Provisioning.Enabled=true`;
- dashboard local acessivel;
- SyncAgent em `not_provisioned` antes da ativacao.

### 4. Ativar pelo dashboard local

Abrir:

```text
http://127.0.0.1:47891/setup
```

Informar:

- URL do ERP de homologacao;
- codigo de ativacao gerado no ERP.

Resultado esperado:

- ativacao HTTP 200;
- estado `provisioned=true`;
- credenciais tecnicas protegidas por DPAPI;
- SyncAgent nao armazena usuario/senha do ERP.

### 5. Validar instalacao limpa

Opcao alternativa para homologacao tecnica por script em VM/maquina limpa:

```powershell
.\infra\invoke-sync-agent-clean-install-homologation.ps1 `
  -PostgresAdminPassword (Read-Host "Senha admin PostgreSQL" -AsSecureString) `
  -DatabasePassword "<senha-local-pdv-sync>" `
  -SkipErp `
  -EvidenceOutput ".\artifacts\sync-agent-clean-install-evidence.json"
```

Com coletor Arpa habilitado:

```powershell
.\infra\invoke-sync-agent-clean-install-homologation.ps1 `
  -PostgresAdminPassword (Read-Host "Senha admin PostgreSQL" -AsSecureString) `
  -DatabasePassword "<senha-local-pdv-sync>" `
  -EnableArpaCollector `
  -ArpaHost "192.168.0.4" `
  -ArpaPort 5432 `
  -ArpaDatabase "anapolis" `
  -ArpaUsername "sync_agent_anapolis_ro" `
  -ArpaPassword (Read-Host "Senha read-only Arpa" -AsSecureString) `
  -SkipErp `
  -EvidenceOutput ".\artifacts\sync-agent-clean-install-evidence.json"
```

Esse script:

- exige PowerShell como Administrador;
- recusa instalacao existente por padrao;
- instala PostgreSQL 17 a partir do payload;
- valida/copia pgvector `0.8.0`;
- instala o SyncAgent em modo `-EnablePostInstallActivation`;
- executa `test-sync-agent-clean-install.ps1`;
- gera evidencia JSON.

Opcao manual, quando a instalacao ja foi feita:

Executar na maquina alvo:

```powershell
.\infra\test-sync-agent-clean-install.ps1 `
  -DatabasePassword "<senha-local-pdv-sync>" `
  -ErpApiBaseUrl "https://erp-homologacao.exemplo.com" `
  -AccessTokenFile "C:\Secrets\sync-agent-status.token" `
  -EvidenceOutput ".\artifacts\sync-agent-clean-install-evidence.json"
```

Se ainda nao houver token para consulta central do ERP:

```powershell
.\infra\test-sync-agent-clean-install.ps1 `
  -DatabasePassword "<senha-local-pdv-sync>" `
  -SkipErp `
  -EvidenceOutput ".\artifacts\sync-agent-clean-install-evidence.json"
```

Resultado esperado:

- servico existe;
- servico esta `Running`;
- payload SyncAgent existe;
- payload Tray existe;
- `appsettings.json` existe;
- `appsettings.json` nao contem access token preenchido;
- `/status` responde `ok`;
- `/setup` responde HTTP 200;
- `/setup` retorna `Cache-Control=no-store`;
- PostgreSQL timezone `America/Sao_Paulo`;
- PostgreSQL major `17`;
- pgvector instalado;
- pgvector versao `0.8.0`;
- schema `sync_agent` criado;
- fila `0`;
- dead-letter `0`;
- ERP `connectivity=online`, quando validacao ERP estiver habilitada.

### 6. Reiniciar servico

```powershell
Restart-Service "PDV Local Sync Agent"
Start-Sleep -Seconds 15
Invoke-RestMethod http://127.0.0.1:47891/status
```

Resultado esperado:

- servico volta `Running`;
- instalacao segue `provisioned=true`;
- runtime nao fica `degraded`;
- fila e dead-letter permanecem controlados.

### 7. Reiniciar Windows

Apos reiniciar:

```powershell
Get-Service "PDV Local Sync Agent"
Invoke-RestMethod http://127.0.0.1:47891/status
```

Resultado esperado:

- servico iniciou automaticamente;
- dashboard local responde;
- tray inicia na sessao do usuario;
- estado provisionado foi preservado.

### 8. Validar rollback

Executar:

```powershell
.\infra\uninstall-sync-agent.ps1
```

Resultado esperado:

- servico removido;
- banco local preservado;
- evidencias e arquivos protegidos nao sao apagados sem decisao explicita.

Validar:

```powershell
Get-Service "PDV Local Sync Agent" -ErrorAction SilentlyContinue
Test-Path "C:\Program Files\PDVLocal"
```

## GO

Pode seguir se todos forem verdadeiros:

- pacote validado;
- instalacao executada como Administrador;
- etapa PostgreSQL do instalador executada com sucesso;
- PostgreSQL major `17` validado;
- pgvector `0.8.0` validado;
- servico `Running`;
- dashboard local responde;
- ativacao pelo `/setup` concluida;
- reinicio do servico preserva `provisioned=true`;
- reinicio do Windows preserva `provisioned=true`;
- banco local com PostgreSQL 17, pgvector e timezone correto;
- fila `0` ou controlada dentro do limite combinado;
- dead-letter `0`;
- ERP `connectivity=online`;
- reconciliacao `matched=true`;
- rollback testado e banco preservado.

## NO-GO

Bloqueia se qualquer item ocorrer:

- instalador precisa de Docker Desktop;
- instalador nao detecta nem instala PostgreSQL 17;
- instalador continua sem pgvector 0.8.0;
- instalador aceita PostgreSQL de major diferente de `17`;
- servico nao instala ou nao inicia;
- dashboard local nao responde;
- ativacao grava usuario/senha do ERP;
- token aparece em `appsettings.json`, log ou evidencia;
- SyncAgent tenta escrever no Arpa;
- usuario Arpa tem permissao de escrita;
- fila cresce continuamente;
- dead-letter com dados reais sem causa conhecida;
- reconciliacao divergente sem explicacao;
- rollback remove banco/evidencias sem autorizacao.

## Evidencias obrigatorias

Salvar junto ao chamado/registro de homologacao:

- `sync-agent-clean-install-evidence.json`;
- JSON de `/status` local;
- status central do ERP;
- print ou export do dashboard local;
- print ou export da tela `/logs`;
- resultado do preflight read-only do Arpa, se coletor estiver habilitado;
- data/hora do reinicio do servico;
- data/hora do reinicio do Windows;
- resultado do rollback.

## Resultado atual

Esta fase ainda depende de execucao em maquina alvo limpa.

Preparado nesta sessao:

- roteiro de homologacao externa;
- script `infra/windows/test-sync-agent-clean-install.ps1`;
- script `infra/windows/build-sync-agent-homologation-bundle.ps1`;
- pacote atualizado com os documentos e scripts.


