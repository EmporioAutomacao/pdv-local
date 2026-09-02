# Manual de Instalacao em Producao - Sync Agent

Este documento e o guia unico, do zero ate o servico rodando em producao,
para instalar o `SyncAgent` (e o `PDV App`) numa maquina Windows de cliente
final. Ele consolida, num fluxo sequencial, o que hoje esta espalhado em:

- `docs/windows-installation.md`
- `docs/sync-agent-sprint-1-manual.md`
- `docs/sync-agent-homologacao-externa.md`
- `docs/sync-agent-piloto-readiness.md`
- `docs/arpa-readonly-security-policy.md`
- `docs/sync-agent-runbook-incidentes.md`
- `docs/sync-agent-ativacao-pos-instalacao.md`

Esses documentos continuam existindo e tem mais detalhe tecnico/historico de
cada etapa. Use este manual como caminho pratico de instalacao; use os
documentos acima quando precisar entender o "porque" de uma etapa ou
diagnosticar um caso fora do previsto aqui.

## 1. Visao geral

A instalacao final, sem Docker, e composta por:

- **PostgreSQL 17** (17.3 ou superior) com extensao **pgvector 0.8.0**,
  banco local `pdv_sync`, timezone `America/Sao_Paulo`.
- **SyncAgent**: Worker .NET 8 instalado como Windows Service
  (`AraraSuiteSync`, nome de exibicao `AraraSuite Sync`), em
  `C:\Program Files\AraraSuite.com.br\Sync\Agent`.
  Expoe um dashboard/API local em `http://127.0.0.1:47891/` (loopback
  apenas, nunca deve ser exposto em interface de rede externa).
- **SyncAgent.Tray**: icone na bandeja do Windows, inicia junto com a sessao
  do usuario, consulta a API local do servico. Instalado em
  `C:\Program Files\AraraSuite.com.br\Sync\Tray`.
- **PDV App**: aplicacao desktop (WPF) instalada em
  `C:\Program Files\AraraSuite.com.br\PDV`.
- Contrato de integracao versionado no repositorio `../sync` (OpenAPI +
  JSON Schemas) — a versao do contrato usada pelo pacote deve ser
  compativel com a versao publicada no ERP.

Fluxo de dados permitido (regra inegociavel de seguranca, ver secao 7):

```text
Arpa -> SyncAgent -> ERP
```

Nunca o inverso (`ERP -> SyncAgent -> Arpa` ou `SyncAgent -> Arpa` para
escrita).

## 2. Aviso de prontidao para producao (ler antes de instalar)

O pacote e o instalador ja sao usados em homologacao/piloto tecnico, mas
existem pendencias conhecidas antes de uma instalacao de producao com
usuario final:

| Item | Status | Impacto |
|---|---|---|
| `SyncAgent.Installer.exe` assinado digitalmente | **Pendente** | Windows/SmartScreen pode alertar o usuario final ao executar o instalador. Definir canal de distribuicao e assinatura de codigo antes do rollout amplo. |
| Certificado cliente / mTLS final do ambiente alvo | **Pendente externo** | Depende do gateway/proxy e do certificado emitido para o cliente especifico. Sem isso, so Bearer token fica disponivel. |
| Senhas padrao de homologacao no instalador interativo | **Trocar antes de producao** | O instalador interativo abre com `postgres`/`pdv_sync` pre-preenchidos (defaults de homologacao). Ver secao 5.1. |
| Usuario read-only do Arpa validado | **Obrigatorio por cliente** | Cada instalacao com coletor Arpa precisa de usuario read-only proprio, validado pelos scripts da secao 7, antes de habilitar o coletor. |

Nao prosseguir com uma instalacao de producao real (dados de cliente,
usuario final sem supervisao tecnica) se qualquer um dos itens acima nao
tiver sido resolvido ou explicitamente aceito como risco pelo responsavel
tecnico do rollout.

## 3. Pre-requisitos

Na maquina alvo:

- Windows com acesso de **Administrador** para a instalacao.
- **PostgreSQL 17.3+** com **pgvector 0.8.0** — pode ser instalado
  manualmente antes, ou pelo proprio instalador interativo (secao 5).
- `psql.exe` disponivel no `PATH` ou caminho conhecido (o instalador tenta
  detectar automaticamente).
- **.NET 8 Runtime**, apenas se os payloads do Agent/Tray tiverem sido
  publicados como `--self-contained false` (padrao do pacote gerado por
  `build-sync-agent-package.ps1`). O pacote instala isso silenciosamente
  quando ausente.
- **Microsoft Visual C++ Redistributable x64** — necessario para `psql.exe`
  funcionar em maquina limpa; o pacote instala isso silenciosamente via
  `payload\VcRuntime\vc_redist.x64.exe`.
- Conectividade **HTTPS** com o ERP de producao (obrigatorio fora de
  `localhost`/redes locais RFC1918).
- **Codigo de ativacao** emitido no ERP para o tenant/instalacao correta
  (gerado pelo time responsavel pelo ERP antes da instalacao).
- Certificado cliente (PFX) emitido, se mTLS estiver habilitado no ambiente
  alvo.
- Se o coletor Arpa for usado: acesso de rede ao banco Arpa do cliente e
  usuario read-only ja preparado ou dados de acesso administrativo para
  prepara-lo durante a instalacao (secao 7).

Portas relevantes:

- `47891` — dashboard/API local do SyncAgent, **loopback apenas**
  (`127.0.0.1`), nunca deve ser liberada no firewall para a rede.
- `5432` — PostgreSQL local (padrao; pode ser outra se configurado).

## 4. Passo 1 - Gerar o pacote de instalacao

Na maquina de build/desenvolvimento (nao na maquina do cliente):

```powershell
.\infra\windows\build-sync-agent-package.ps1 -Version X.Y.Z
```

Isso publica `SyncAgent`, `SyncAgent.Tray`, `PDV App` (framework-dependent) e
`SyncAgent.Installer` (self-contained, single-file), e monta o pacote em:

```text
artifacts\sync-agent-installer\
```

contendo: payloads publicados, .NET 8 Desktop Runtime, VC++ Redistributable,
PostgreSQL 17 + pgvector 0.8.0, scripts de instalacao/desinstalacao/smoke
test, SQL de bootstrap, `self-update.ps1`, arquivo `VERSION` e copia dos
guias operacionais.

Com `-Version X.Y.Z` tambem sao gerados `pdv-local-vX.Y.Z.zip` e
`pdv-local-vX.Y.Z.zip.sha256` (usados pelo auto-update, ver secao 10).

Para release automatizado via GitHub Actions, em vez de rodar o script
manualmente:

```powershell
git tag vX.Y.Z
git push origin vX.Y.Z
```

O workflow `.github/workflows/release.yml` (repo publico
`github.com/EmporioAutomacao/pdv-local`) baixa as dependencias grandes
(PostgreSQL 17, pgvector, runtime .NET, VC++ redist) com cache, builda os dois
artefatos e cria o Release:

| Arquivo | Uso |
|---|---|
| `pdv-local-vX.Y.Z.zip` (+ `.sha256`) | Pacote de **auto-update** (Tray > Atualizar App). |
| `PdvLocalInstaller-vX.Y.Z.exe` (+ `.sha256`) | Instalador completo para maquina nova. |

O `download_url` do `SyncPackage` a registrar no ERP fica
`https://github.com/EmporioAutomacao/pdv-local/releases/download/vX.Y.Z/pdv-local-vX.Y.Z.zip`.
Da para disparar o workflow sem tag pela aba Actions ("Run workflow", campo
`version`).

**Antes de levar o pacote para a maquina do cliente**, valide o layout sem
exigir Administrador e sem alterar nada:

```powershell
cd artifacts\sync-agent-installer
.\infra\install-sync-agent.ps1 -ValidateOnly
```

Confirme tambem o SHA256 do pacote transferido contra o `.sha256` gerado,
antes de instalar.

## 5. Passo 2 - Instalar

Ha dois caminhos. Para producao com usuario final, use o **instalador
interativo**; o caminho PowerShell fica reservado para automacao e suporte
tecnico.

### 5.1 Instalador interativo (recomendado)

Copie o pacote para a maquina alvo e execute como Administrador:

```powershell
.\payload\SyncAgentInstaller\SyncAgent.Installer.exe
```

Telas do assistente:

1. **Caminho de instalacao.**
2. **Etapa PostgreSQL** — escolher "usar PostgreSQL 17 ja instalado" ou
   "instalar PostgreSQL 17 automaticamente". No modo "ja instalado", o
   instalador valida `psql.exe`, versao major `17`, conexao admin e
   extensao `vector` na versao `0.8.0` antes de avancar — a instalacao do
   SyncAgent so prossegue depois dessas validacoes passarem.
3. **Dados do banco local** `pdv_sync` (usuario/senha).
4. **Coletor Arpa (opcional)** — Host, Porta, Database, Username, senha
   read-only em campo protegido; botoes `Testar conexao`, `Preparar views`,
   `Criar usuario` (ver secao 7 antes de usar esses botoes).
5. **Revisao** — manter marcada a opcao "Executar validacao de instalacao
   ao concluir" (roda `test-sync-agent-clean-install.ps1 -SkipErp` e grava
   `sync-agent-clean-install-evidence.json`).

O instalador sempre habilita `-EnablePostInstallActivation`: ele instala o
servico e prepara o banco, mas deixa a conexao com o ERP para o passo de
ativacao (secao 8), sem pedir usuario/senha do ERP em nenhum momento.

**Atencao — defaults de homologacao**: para reduzir atrito em VM de
teste/piloto tecnico, o instalador interativo abre com senha admin
PostgreSQL `postgres` e senha do usuario local `pdv_sync` ja preenchidas.
**Antes de uma instalacao de producao real, substitua esses valores por
senha gerada/definida pela politica de credenciais do cliente** — nao
aceitar os defaults de homologacao em instalacao de producao.

O instalador tambem instala silenciosamente o .NET 8 Desktop Runtime e o
VC++ Redistributable x64 quando ausentes na maquina.

### 5.2 PowerShell (automacao/suporte)

Instalacao completa via script, para cenarios automatizados:

```powershell
.\infra\windows\install-sync-agent.ps1 `
  -InstanceId "<instance-id>" `
  -ErpTenantId "<tenant-id>" `
  -ErpApiBaseUrl "https://erp.cliente.com" `
  -AccessToken "<token-emitido-pelo-ERP>" `
  -ClientCertificateThumbprint "<thumbprint-certificado-cliente>" `
  -PostgresHost localhost `
  -PostgresPort 5432 `
  -PostgresAdminUser postgres `
  -PostgresAdminPassword (Read-Host "Senha admin PostgreSQL" -AsSecureString) `
  -DatabaseName pdv_sync `
  -DatabaseUser pdv_sync `
  -DatabasePassword "<senha-gerada-pdv-sync>"
```

Ou, para instalar sem credenciais do ERP e ativar depois pelo dashboard
(equivalente ao fluxo do instalador interativo):

```powershell
.\infra\windows\install-sync-agent.ps1 `
  -EnablePostInstallActivation `
  -PostgresAdminPassword (Read-Host "Senha admin PostgreSQL" -AsSecureString) `
  -DatabasePassword "<senha-gerada-pdv-sync>"
```

Parametros principais do script (`infra\windows\install-sync-agent.ps1`):

| Parametro | Default | Descricao |
|---|---|---|
| `-InstallRoot` | `C:\Program Files\AraraSuite.com.br` | Raiz de instalacao. |
| `-ServiceName` / `-ServiceDisplayName` | `AraraSuiteSync` / `AraraSuite Sync` | Nome do Windows Service. |
| `-InstanceId`, `-ErpTenantId`, `-ErpApiBaseUrl` | — | Identidade do agente e URL do ERP. |
| `-AgentVersion` | `1.0.0` | Versao gravada no `appsettings.json`. |
| `-AccessToken` | vazio | Token tecnico (ignorado se usar ativacao pos-instalacao). |
| `-TokenEnvironmentVariable` | `PDV_SYNC_ERP_ACCESS_TOKEN` | Variavel de ambiente de maquina onde o token e lido. |
| `-ClientCertificateThumbprint`, `-PfxPath`, `-PfxPassword` | vazio | Certificado cliente para mTLS. |
| `-EnablePostInstallActivation` | desligado | Instala sem credenciais do ERP; ativacao via `/setup` depois. |
| `-ProvisioningProtectedFile` | caminho padrao em `Secrets\` | Override do caminho do arquivo DPAPI de provisionamento. |
| `-PostgresHost/-Port/-AdminUser/-AdminPassword` | `localhost`/`5432`/`postgres`/— | Acesso admin ao PostgreSQL local para bootstrap. |
| `-DatabaseName/-DatabaseUser/-DatabasePassword` | `pdv_sync`/`pdv_sync`/`pdv_sync` | Banco e usuario local do SyncAgent. |
| `-PsqlPath` | `psql` | Caminho do executavel `psql`, se nao estiver no `PATH`. |
| `-EnableArpaCollector` | desligado | Habilita coletor Arpa (ver secao 7). |
| `-ArpaConnectionString`, `-ArpaPassword`, `-ArpaPasswordProtectedFile` | — | Conexao Arpa read-only. |
| `-ArpaCollectorPreset` | `None` | `None` ou `AnapolisInitialLoad`. |
| `-ArpaBatchSize` | `100` | Tamanho de lote do coletor (piloto Anapolis usa `5000` na carga inicial). |
| `-SkipDatabaseBootstrap` | desligado | Pula o bootstrap do banco (quando ja preparado). |
| `-SkipServiceStart` | desligado | Instala sem iniciar o servico. |
| `-SkipTrayStartup` | desligado | Nao registra o tray na inicializacao. |
| `-ValidateOnly` | desligado | So valida o layout do pacote, nao instala nada. |

O script, ao instalar:

- aplica o bootstrap do banco local (a menos que `-SkipDatabaseBootstrap`);
- provisiona o token como variavel de ambiente de maquina quando informado;
- importa o PFX quando `-PfxPath` for informado;
- protege a senha read-only do Arpa com DPAPI `LocalMachine` quando o
  coletor estiver habilitado;
- copia os artefatos para `C:\Program Files\AraraSuite.com.br` (Sync\Agent,
  Sync\Tray e PDV);
- grava `appsettings.json` provisionado do SyncAgent e do PDV App;
- instala/atualiza o Windows Service `AraraSuiteSync` (AraraSuite Sync) com
  restart automatico em falha;
- cria atalho do tray na inicializacao do Windows e atalhos
  `AraraSuite PDV.lnk` na area de trabalho publica e no Menu Iniciar.

### 5.3 Seguranca de credenciais em producao

Em producao, **as credenciais nao devem ficar em texto puro no
`appsettings.json`**. Use:

```powershell
.\infra\windows\provision-sync-agent-security.ps1 `
  -AccessToken "<token-curto-ou-inicial>" `
  -PfxPath "C:\Install\sync-agent-client.pfx" `
  -PfxPassword (Read-Host "Senha do PFX" -AsSecureString)
```

Isso grava o token na variavel de ambiente de maquina
`PDV_SYNC_ERP_ACCESS_TOKEN` e importa o certificado no Windows Certificate
Store **sem marcar a chave privada como exportavel**. Configure depois
`ErpSecurity:ClientCertificateThumbprint` no `appsettings.json` com o
thumbprint exibido pelo script. Nunca registrar token, senha de PFX ou
payload de cliente em log operacional.

## 6. Passo 3 - Banco de dados

O bootstrap (rodado automaticamente pelo instalador, a menos que
`-SkipDatabaseBootstrap` seja usado) aplica, nesta ordem:

- `infra/postgres/init/00-set-timezone.sql` — timezone `America/Sao_Paulo`.
- `infra/postgres/init/01-create-vector-extension.sql` — extensao `vector`.
- `infra/postgres/init/02-create-sync-agent-schema.sql` — schema
  `sync_agent` (`outbox_events`, `dispatch_attempts`, `inbox_events`,
  `agent_state`, `reconciliation_runs`, `dead_letter_events`).
- `infra/postgres/init/03-create-pdv-schema.sql` — schema `pdv`
  (`operators`, `cash_sessions`, `cash_movements`, `products`, `customers`,
  `sales`, `sale_items`, `payments`, entre outras).

Para rodar manualmente (banco ja existente ou `-SkipDatabaseBootstrap`):

```powershell
.\infra\windows\bootstrap-sync-agent-db.ps1 `
  -PostgresHost localhost `
  -PostgresPort 5432 `
  -AdminUser postgres `
  -DatabaseName pdv_sync `
  -DatabaseUser pdv_sync `
  -DatabasePassword "<senha-gerada-pdv_sync>"
```

Validacao manual apos o bootstrap:

```powershell
psql -U pdv_sync -d pdv_sync -c "SELECT extname, extversion FROM pg_extension WHERE extname = 'vector';"
psql -U pdv_sync -d pdv_sync -c "SHOW timezone;"
psql -U pdv_sync -d pdv_sync -c "SELECT table_name FROM information_schema.tables WHERE table_schema = 'sync_agent' ORDER BY table_name;"
psql -U pdv_sync -d pdv_sync -c "SELECT table_name FROM information_schema.tables WHERE table_schema = 'pdv' ORDER BY table_name;"
```

## 7. Passo 4 - Coletor Arpa (opcional)

Aplicavel apenas quando a instalacao precisa ler dados do sistema Arpa do
cliente (produtos/clientes/estoque). Se o cliente nao usa Arpa, pule esta
secao (deixe `-EnableArpaCollector` desligado).

**Regra inegociavel** (`docs/arpa-readonly-security-policy.md`): o SyncAgent
**nunca** grava dados no Arpa. Fluxo permitido:

```text
Arpa -> SyncAgent -> ERP
```

O usuario runtime usado pelo SyncAgent no Arpa deve ter **somente**
`CONNECT`, `USAGE` no schema de exportacao e `SELECT` nas views
`sync_export.*` — nunca `INSERT/UPDATE/DELETE/CREATE/ALTER/DROP`,
ownership ou superuser. As views (`sync_export.produtos`,
`sync_export.clientes`, `sync_export.estoque`) sao o contrato de leitura,
definido em `infra\arpa\sync-export-views-contract.sql`.

Checklist obrigatorio antes de habilitar o coletor:

1. Confirmar que existe um usuario read-only dedicado (nunca reaproveitar
   `postgres` ou o usuario do sync direto do ERP como credencial runtime).
2. Confirmar que esse usuario nao e dono de tabelas/views.
3. Confirmar que `INSERT/UPDATE/DELETE/CREATE/ALTER/DROP` foram negados.
4. Rodar o preflight de permissao:

   ```powershell
   .\infra\windows\test-arpa-readonly-permissions.ps1 `
     -PostgresHost "<host-arpa>" `
     -DatabaseName "<database-arpa>" `
     -DatabaseUser "<usuario-read-only>"
   ```

5. Rodar o preflight das views:

   ```powershell
   .\infra\windows\test-arpa-export-preflight.ps1 `
     -PostgresHost "<host-arpa>" `
     -DatabaseName "<database-arpa>" `
     -DatabaseUser "<usuario-read-only>"
   ```

6. Habilitar o coletor somente depois das validacoes acima passarem.

No instalador interativo, os botoes `Preparar views` e `Criar usuario` (tela
do coletor Arpa) automatizam a preparacao administrativa (conectando como
`postgres` sem senha, conforme padrao do fabricante do Arpa) — isso e uma
atividade de DBA, distinta do runtime do SyncAgent, e nunca deve ser
confundida com a credencial usada pelo servico depois de instalado.

Exemplo de instalacao via PowerShell com coletor Arpa habilitado:

```powershell
.\infra\windows\install-sync-agent.ps1 `
  -InstanceId "<instance-id>" `
  -ErpTenantId "<tenant-id>" `
  -ErpApiBaseUrl "https://erp.cliente.com" `
  -AccessToken "<token-emitido-pelo-ERP>" `
  -PostgresAdminPassword (Read-Host "Senha admin PostgreSQL" -AsSecureString) `
  -DatabasePassword "<senha-gerada-pdv_sync>" `
  -EnableArpaCollector `
  -ArpaConnectionString "Host=<host-arpa>;Port=5432;Database=<database-arpa>;Username=<usuario-read-only>" `
  -ArpaPassword (Read-Host "Senha read-only Arpa" -AsSecureString) `
  -ArpaBatchSize 100
```

A senha do Arpa e gravada protegida por DPAPI em
`C:\Program Files\AraraSuite.com.br\Sync\Secrets\arpa-runtime-password.dpapi`
— nunca em texto puro no `appsettings.json`.

## 8. Passo 5 - Ativacao pos-instalacao

Apos a instalacao (interativa ou via PowerShell com
`-EnablePostInstallActivation`), o servico fica no estado
`not_provisioned` e nao executa coleta, envio, heartbeat remoto ou
reconciliacao remota ate ser ativado.

Abrir no navegador da maquina instalada:

```text
http://127.0.0.1:47891/setup
```

Informar:

- URL do ERP de producao (deve usar HTTPS fora de `localhost`/`127.0.0.1`);
- codigo de ativacao gerado no ERP para o tenant/instalacao correta.

Resultado esperado:

- ativacao HTTP `200`;
- estado passa a `provisioned=true`;
- credenciais tecnicas (access/refresh token) protegidas por DPAPI
  `LocalMachine`;
- o SyncAgent **nunca** armazena usuario/senha do ERP — apenas as
  credenciais tecnicas emitidas apos a ativacao.

## 9. Passo 6 - Validar a instalacao

Verificacoes basicas:

```powershell
Get-Service "AraraSuiteSync"
Invoke-RestMethod -Uri "http://127.0.0.1:47891/status" -Method Get
```

Smoke test local (sem ERP):

```powershell
.\infra\windows\test-sync-agent-smoke.ps1 -SkipErp
```

Smoke test completo com ERP:

```powershell
.\infra\windows\test-sync-agent-smoke.ps1 `
  -ErpApiBaseUrl "https://erp.cliente.com" `
  -InstanceId "<instance-id>" `
  -AccessToken "<token-emitido-pelo-ERP>"
```

Para validar prontidao de release/producao (o check mais completo — pacote,
contrato, dashboard local, logs, fila, dead-letter, status central do ERP e
reconciliacao), sem expor o token na evidencia gerada:

```powershell
.\infra\windows\test-sync-agent-release-readiness.ps1 `
  -ErpApiBaseUrl "https://erp.cliente.com" `
  -InstanceId "<instance-id>" `
  -AccessTokenFile "C:\Secrets\sync-agent.token" `
  -EvidenceOutput ".\artifacts\sync-agent-release-readiness.json"
```

Guarde essa evidencia junto ao registro de rollout do cliente. Considere a
instalacao pronta para producao apenas se, alem do checklist da secao 2:

- servico `Running`;
- dashboard local responde;
- ativacao concluida (`provisioned=true`);
- PostgreSQL major `17`, pgvector `0.8.0`, timezone `America/Sao_Paulo`;
- fila (`pending_outbox_events`) controlada, sem crescimento continuo;
- `dead_letter_events=0` no inicio da operacao;
- ERP retorna `connectivity=online` para a instalacao;
- reconciliacao remota `matched=true`;
- reinicio do servico e reinicio do Windows preservam `provisioned=true`
  (testar ao menos uma vez antes de liberar a maquina para o usuario final).

## 10. Operacao

Windows Service:

```powershell
Get-Service "AraraSuiteSync"
Restart-Service "AraraSuiteSync"
```

Tray (`SyncAgent.Tray`): inicia na sessao do usuario, consulta a mesma API
local (`http://127.0.0.1:47891`). Nao precisa de acao manual apos a
instalacao.

Auto-update: o pacote inclui `self-update.ps1` dentro do payload do
SyncAgent. Quando um pacote versionado (`pdv-local-vX.Y.Z.zip`) e
registrado/disponibilizado para download, o agente detecta a nova versao no
proximo heartbeat e se auto-atualiza.

Endpoints uteis do dashboard/API local (sempre `127.0.0.1:47891`):

| Endpoint | Metodo | Uso |
|---|---|---|
| `/` | GET | Dashboard HTML. |
| `/status` | GET | Status runtime, fila, dead-letter, ultima reconciliacao. |
| `/setup` | GET | Tela de ativacao pos-instalacao. |
| `/setup/activate` | POST | Ativa a instalacao (URL do ERP + codigo de ativacao). |
| `/logs` | GET | Log operacional recente. |
| `/sync-now` | POST | Dispara coleta+dispatch imediato (retorna `202` ou `409`). |
| `/check-update` | POST | Forca verificacao de nova versao. |
| `/help` | GET | Ajuda/diagnostico local. |

## 11. Desinstalacao / rollback

Por padrao, remove o servico e **preserva** banco/arquivos (outbox,
dead-letter, auditoria, evidencias):

```powershell
.\infra\windows\uninstall-sync-agent.ps1
```

Para remover tambem o atalho do tray:

```powershell
.\infra\windows\uninstall-sync-agent.ps1 -RemoveTrayStartup
```

Para remover tambem os arquivos instalados em `C:\Program Files\AraraSuite.com.br`
(irreversivel para os artefatos, banco continua preservado):

```powershell
.\infra\windows\uninstall-sync-agent.ps1 -RemoveTrayStartup -RemoveFiles
```

Validar apos a desinstalacao:

```powershell
Get-Service "AraraSuiteSync" -ErrorAction SilentlyContinue
Test-Path "C:\Program Files\AraraSuite.com.br"
```

O banco `pdv_sync` nao e removido automaticamente por nenhuma dessas
opcoes — remocao de banco exige decisao explicita e backup previo.

## 12. Troubleshooting

Guia completo em `docs/sync-agent-runbook-incidentes.md`. Resumo dos
incidentes mais comuns:

| Sintoma | Causa provavel | Primeira acao |
|---|---|---|
| ERP mostra `connectivity=offline`, `/status` nao responde | Servico Windows parado | `Get-Service` / `Start-Service "AraraSuiteSync"` |
| `pending_outbox_events` crescendo continuamente | Rede/DNS ao ERP, token/certificado invalido | Validar `ErpApiBaseUrl`, token/certificado; forcar `POST /sync-now` |
| `dead_letter_events > 0` | Payload invalido ou dependencia ausente no ERP | Consultar `sync_agent.dead_letter_events`; nunca apagar sem decisao tecnica |
| Reconciliacao `matched=false` | Divergencia de contadores/hash entre local e ERP | Comparar `mismatches` no ERP; nunca reprocessar sem identificar a causa |
| HTTP `401`/`403` do ERP | Token expirado/invalido ou certificado ausente | Confirmar `PDV_SYNC_ERP_ACCESS_TOKEN` na maquina; rotacionar token se necessario |
| Dashboard mostra `not_provisioned` apos instalacao | Ativacao nao concluida ou codigo expirado | Reabrir `/setup`, gerar novo codigo de ativacao se necessario |
| `runtime_status=degraded` com `permission denied for relation produtos/clientes` | Usuario Arpa sem permissao correta | **Nao trocar para `postgres`**; revalidar com `test-arpa-readonly-permissions.ps1` e reaplicar apenas grants via DBA |

Regras de seguranca no suporte: nunca compartilhar Bearer token ou PFX em
chamados; nunca remover banco local sem backup e autorizacao; nunca fazer
`DELETE` em outbox/DLQ/reconciliation sem plano de recuperacao.

## 13. Referencias

- `docs/windows-installation.md` — guia tecnico original de instalacao
  Windows (mais detalhe sobre o instalador de PostgreSQL/pgvector).
- `docs/sync-agent-sprint-1-manual.md` — arquitetura completa, todos os
  endpoints da API local, configuracao detalhada do `appsettings.json`.
- `docs/sync-agent-homologacao-externa.md` — roteiro de homologacao em
  maquina limpa, criterios GO/NO-GO, evidencias obrigatorias.
- `docs/sync-agent-piloto-readiness.md` — gates de prontidao para piloto
  tecnico, bloqueios conhecidos.
- `docs/sync-agent-ativacao-pos-instalacao.md` — desenho detalhado do fluxo
  de ativacao (`/setup`, DPAPI, estados do agente).
- `docs/arpa-readonly-security-policy.md` — politica de seguranca read-only
  do Arpa.
- `docs/sync-agent-runbook-incidentes.md` — runbook completo de incidentes
  operacionais.
- `../sync/README.md` e `../sync/CHANGELOG.md` — contrato de integracao
  versionado entre ERP e PDV local.
