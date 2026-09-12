# Piloto Arpa - Anapolis

Data: 31/05/2026

## Escopo

Conexao escolhida para o primeiro piloto real:

```text
ERP ArpaControlConexao id: 1
nome: Anapolis
host: 192.168.0.4:5432
banco: anapolis
```

O SyncAgent continua estritamente read-only no Arpa:

```text
Arpa -> SyncAgent -> ERP
```

## Artefatos

SQL de views — **arquivo unico e generico** (introspecta o schema, gera views
incrementais com preco; roda pelo botao "Preparar views" ou pelo script de
preparo do piloto):

```text
infra/arpa/sync-export-views.sql
```

Template para usuario runtime read-only:

```text
infra/arpa/sync-export-readonly-user-anapolis.template.sql
```

Politica de seguranca:

```text
docs/arpa-readonly-security-policy.md
```

Decisao de watermark:

```text
docs/arpa-produtos-watermark-decision.md
```

## Plano de execucao

1. DBA/admin revisa o SQL.
2. DBA/admin aplica o SQL no banco `anapolis`.
3. DBA/admin cria ou ajusta usuario runtime read-only para o SyncAgent.
4. Suporte valida permissoes read-only.
5. Suporte valida views com preflight.
6. SyncAgent e configurado com collector habilitado e dispatcher desligado.
7. Conferir outbox local.
8. Habilitar dispatcher.
9. Validar ERP/reconciliacao.

Opcao operacional consolidada:

```powershell
.\infra\windows\prepare-arpa-anapolis-pilot.ps1 `
  -DbaUser "<usuario-dba>" `
  -GenerateRuntimePassword `
  -ConfirmApply
```

Esse script aplica as views e cria/ajusta o usuario `sync_agent_anapolis_ro`.
Ele exige `-ConfirmApply`, bloqueia usuario runtime como DDL, gera senha forte
unica para o usuario read-only e nao grava dados de negocio no Arpa.

Se o Arpa legado aceitar `postgres` sem senha, essa condicao deve ser tratada
como excecao temporaria de DBA apenas para preparacao. Nesse caso, nao definir
`ARPA_ANAPOLIS_DBA_PASSWORD` e executar com flag explicita:

```powershell
.\infra\windows\prepare-arpa-anapolis-pilot.ps1 `
  -DbaUser "postgres" `
  -AllowEmptyDbaPassword `
  -GenerateRuntimePassword `
  -ConfirmApply
```

Mesmo nesse cenario, o SyncAgent nunca deve usar `postgres`. O runtime continua
obrigatoriamente com usuario separado `sync_agent_anapolis_ro`, permissao apenas
de leitura nas views `sync_export` e senha forte guardada no Windows.

Por padrao, a senha gerada e gravada localmente em:

```text
.secrets/arpa/anapolis-runtime-password.txt
```

Essa pasta e ignorada pelo Git. O arquivo so e gravado depois que views e
usuario read-only forem aplicados com sucesso; se a preparacao falhar, a senha
gerada e descartada.

Em Windows, o padrao operacional do SyncAgent e usar a senha protegida por
DPAPI LocalMachine:

```powershell
.\infra\windows\protect-sync-agent-secret.ps1 `
  -InputFile .\.secrets\arpa\anapolis-runtime-password.txt `
  -OutputFile .\.secrets\arpa\anapolis-runtime-password.dpapi
```

O script do piloto (`start-sync-agent-anapolis-pilot.ps1`) usa
`.secrets/arpa/anapolis-runtime-password.dpapi` quando o arquivo existir. O
arquivo `.txt` continua sendo apenas fallback operacional temporario e nao deve
ser colocado no Git.

Pre-requisitos na maquina de execucao:

- `psql` disponivel no PATH, ou informar `-PsqlPath "C:\...\psql.exe"`;
- alternativamente, baixar os binarios oficiais PostgreSQL 17.10 em
  `artifacts/postgresql-17/extracted`, onde o script localiza automaticamente
  `pgsql/bin/psql.exe`;
- variavel `ARPA_ANAPOLIS_DBA_PASSWORD` definida somente na sessao atual, exceto
  quando o DBA autorizar `postgres` sem senha com `-AllowEmptyDbaPassword`;
- `-GenerateRuntimePassword` para gerar senha read-only unica, ou variavel
  `ARPA_SYNC_READONLY_PASSWORD` definida somente na sessao atual;
- usuario DBA/admin autorizado em `-DbaUser`;
- aprovacao explicita para executar com `-ConfirmApply`.

Status local em 31/05/2026: binarios oficiais PostgreSQL 17.10 baixados e
extraidos em `artifacts/postgresql-17/extracted`; `psql --version` retornou
`psql (PostgreSQL) 17.10`. A senha runtime agora pode ser gerada
automaticamente por instalacao com `-GenerateRuntimePassword`.

Status de preparacao real em 31/05/2026:

- banco `anapolis` acessado em `192.168.0.4:5432` com DBA temporario
  `postgres` sem senha;
- schema `sync_export` criado;
- views `sync_export.produtos`, `.clientes` e `.estoque` criadas/substituidas
  pelo script unico (1.6.9+): `occurred_at_utc` vem das tabelas de log
  `alterados` / `alterados_clientes` / `produtos_altera_quantidade` ->
  **sincronizacao incremental** (preco, saldo, cadastro). `produtos` inclui
  `precocusto`/`precovenda`. Fuso fixo `Etc/GMT+3` (o `America/Sao_Paulo` do
  PG 9.6 do Arpa aplica DST fantasma e erra 1h no verao).
- usuario runtime read-only criado/ajustado, com `GRANT SELECT` nas views
  (o proprio "Preparar views" ja concede);
- ~~vendas/financeiro: sem tabelas mapeaveis nesse Arpa -> toggles
  desmarcados~~ **desatualizado, ver "Status em 2026-09-11" abaixo**: as
  tabelas existem, so nao no formato que o script original assumia.

Status de seguranca em 01/06/2026:

- senha runtime protegida localmente em
  `.secrets/arpa/anapolis-runtime-password.dpapi`;
- SyncAgent validado usando `ArpaCollector:PasswordProtectedFile`;
- arquivo protegido usa Windows DPAPI `LocalMachine`;
- dashboard, logs e reconciliacao seguiram saudaveis apos reinicio do agente.
- grants read-only em `sync_export.produtos` e `sync_export.clientes`
  reaplicados e validados apos detectar perda de `SELECT` para
  `sync_agent_anapolis_ro`; nenhuma permissao de escrita foi concedida.

Para repetir o download em outra maquina:

```powershell
.\infra\windows\download-postgresql17-binaries.ps1
```

Fonte oficial usada:

```text
https://get.enterprisedb.com/postgresql/postgresql-17.10-1-windows-x64-binaries.zip
```

## Aplicacao das views

Usar usuario DBA/admin, nao o usuario runtime do SyncAgent:

```powershell
$env:ARPA_SYNC_READONLY_PASSWORD = "<senha-ddl-temporaria>"
.\infra\windows\apply-arpa-sync-export-views.ps1 `
  -SqlFile ".\infra\arpa\sync-export-views.sql" `
  -PostgresHost "192.168.0.4" `
  -DatabaseName "anapolis" `
  -ExpectedDatabaseName "anapolis" `
  -DatabaseUser "<usuario-dba>" `
  -ConfirmApply
```

Depois de aplicar as views, remover ou nao usar mais a credencial DBA no
processo do SyncAgent.

## Usuario runtime

A conexao Anapolis cadastrada atualmente no ERP usa o usuario `postgres`.
Diagnostico executado em 31/05/2026:

```text
current_user: postgres
is_superuser: true
can_create_public: true
produtos_insert/update/delete: true
clientes_insert/update/delete: true
is_runtime_readonly_safe: false
```

Esse usuario **nao pode** ser usado pelo SyncAgent.

O DBA deve criar um usuario runtime separado usando o template:

```text
infra/arpa/sync-export-readonly-user-anapolis.template.sql
```

Depois, validar com:

```powershell
$env:ARPA_SYNC_READONLY_PASSWORD = Get-Content .\.secrets\arpa\anapolis-runtime-password.txt -Raw
.\infra\windows\test-arpa-readonly-permissions.ps1 `
  -PostgresHost "192.168.0.4" `
  -DatabaseName "anapolis" `
  -DatabaseUser "sync_agent_anapolis_ro"
```

## Validar usuario read-only

```powershell
$env:ARPA_SYNC_READONLY_PASSWORD = Get-Content .\.secrets\arpa\anapolis-runtime-password.txt -Raw
.\infra\windows\test-arpa-readonly-permissions.ps1 `
  -PostgresHost "192.168.0.4" `
  -DatabaseName "anapolis" `
  -DatabaseUser "sync_agent_anapolis_ro"
```

## Validar views

```powershell
$env:ARPA_SYNC_READONLY_PASSWORD = Get-Content .\.secrets\arpa\anapolis-runtime-password.txt -Raw
.\infra\windows\test-arpa-export-preflight.ps1 `
  -PostgresHost "192.168.0.4" `
  -DatabaseName "anapolis" `
  -DatabaseUser "<usuario-read-only>"
```

## Configurar collector

Usar `infra/arpa/arpa-collector.piloto.template.json` como base e preencher:

```text
Host=192.168.0.4
Database=anapolis
Username=sync_agent_anapolis_ro
PasswordFile=.secrets/arpa/anapolis-runtime-password.txt
```

Para Windows, preferir:

```text
PasswordProtectedFile=.secrets/arpa/anapolis-runtime-password.dpapi
```

Nao colocar `Password=<senha>` no `appsettings.json` nem na connection string.

Para o primeiro ciclo real, manter:

```text
ErpDispatcher:Enabled=false
```

Depois de conferir outbox local e ausencia de dead-letter, habilitar dispatcher.

## Operacao local repetivel

Para iniciar a Sync API do ERP em desenvolvimento, no repositorio `erp`:

```powershell
.\scripts\dev\start-sync-api-dev.ps1 -StopExisting
```

Para iniciar o SyncAgent do piloto Anapolis, no repositorio `pdv-local`:

```powershell
.\infra\windows\start-sync-agent-anapolis-pilot.ps1 -StopExisting
```

Para instalacao Windows provisionada com coletor Arpa habilitado:

```powershell
.\infra\windows\install-sync-agent.ps1 `
  -InstanceId "anapolis-local-test-01" `
  -ErpTenantId "piloto-anapolis" `
  -ErpApiBaseUrl "https://erp.exemplo.com" `
  -AccessToken "<token-emitido-pelo-ERP>" `
  -PostgresAdminPassword (Read-Host "Senha admin PostgreSQL" -AsSecureString) `
  -DatabasePassword "<senha-local-pdv-sync>" `
  -EnableArpaCollector `
  -ArpaConnectionString "Host=192.168.0.4;Port=5432;Database=anapolis;Username=sync_agent_anapolis_ro" `
  -ArpaPassword (Read-Host "Senha read-only Arpa" -AsSecureString) `
  -ArpaCollectorPreset AnapolisInitialLoad `
  -ArpaBatchSize 5000
```

Para validar ERP, dashboard, logs e reconciliacao:

```powershell
.\infra\windows\test-anapolis-pilot-health.ps1
```

Resultado esperado:

```text
erp_connectivity: online
erp_queue_size: 0
erp_rejected_events_24h: 0
erp_last_reconciliation_matched: True
local_pending: 0
local_dead_letter: 0
local_logs_http_status: 200
```

## Status em 2026-09-11

Investigacao completa do schema real do Anapolis (usuario runtime, apos GRANT
temporario descrito abaixo) confirmou e corrigiu o que a entrada de
31/05/2026 registrou como "sem tabelas mapeaveis":

- **Usuario runtime atual**: `ararasuite_sync_ro` (o nome de exemplo
  `sync_agent_anapolis_ro` usado nas secoes acima ficou desatualizado -
  confirme sempre em Configuracoes > Arpa > editar conexao).
- **Todas as 7 views agora existem**: `produtos`, `clientes`, `estoque`,
  `plano_historico`, `vendas`, `financeiro`, `cobranca` (confirmado por
  "Preparar views" na conexao "Geral"). So `cobranca` ficou de fora antes -
  ver abaixo.
- **Schema real de vendas/financeiro** (normalizado em varias tabelas, nao
  uma unica tabela com tudo - por isso o script generico original nunca
  achava): `cabecalho_ordem_servico` + `itens_ordem_servico` (venda);
  `parcelas` + `parcelas_quitadas` (financeiro a receber - cliente vem de
  `cabecalho_ordem_servico.cliente` via `parcelas.ordem`, nao de uma coluna
  direta); `apagar` + `apagar_quitadas` (financeiro a pagar - vinculo com a
  compra via `apagar_nota_compra.nota -> cabecalho_nota_compra.nota`, nao
  uma coluna de ordem direta em `apagar`).
- **Schema real de cobranca**: `ctabancarias` (conta+banco) +
  `bancos` (agencia/num_banco) + `convenio_bancos` (carteira/convenio/
  cedente), com `convenio_dados`/`conf_boletos_dll` opcionais. `ctabancarias`
  sozinha nunca tinha os campos - por isso `sync_export.cobranca` sempre
  pulava com "sem id/agencia/conta/banco" ate o SyncAgent 1.6.17
  (`infra/arpa/sync-export-views.sql` ganhou fallback via join para esse
  formato).
- `infra/arpa/sync-export-views.sql` (1.6.17-1.6.19) ganhou fallback
  dinamico para vendas/financeiro/cobranca quando o schema fixo original nao
  bate - testado com Postgres descartavel reproduzindo esse schema real
  antes de publicar.
- **Resolvido**: o usuario rodou a rotina "Corrigir
  outbox_events_entity_type_check" (Configuracoes > Manutencao do banco
  local, 1.6.20+) na VM. Confirmado por leitura direta da constraint
  (`pg_get_constraintdef`, credencial `pdv_sync` normal) e pelas contagens
  reais em `sync_agent.outbox_events` no mesmo dia: `cobranca` 6 accepted,
  `plano_historico` 150 accepted, `venda` 4191 accepted, `financeiro` 6675
  accepted, `produto` 34632 accepted - as 7 entidades fluindo Arpa -> local
  -> ERP de ponta a ponta.
- **`entity_type=produto` ganhou o campo `unidade`** (contrato Sync 2.13.0,
  SyncAgent 1.6.24, ERP `sync_api.domain_processor.apply_produto` +
  `resolve_unidade_arpa`). A tabela `produtos` do Anapolis tem coluna
  `unidade` (varchar) - confirmado por leitura direta na view
  (`sync_export.produtos` ja retorna `"unidade": "UN"/"UND"` etc.).
  Produtos ja sincronizados antes desse deploy so ganham o campo quando
  mudarem de novo ou num resync (ver "Sincronizar tudo" com periodo,
  1.6.25, em `docs/sync-agent-configuracoes-arpa.md`).
- **Nao e bug**: uma amostra pequena (3 produtos) do payload mostrou
  `"ativo": false` nos tres e pareceu um sinal de que a logica de
  `ativo` invertida (`p_active` generico, ver `sync-export-views.sql`)
  estava errada. Conferido em agregado: `produtos.ativo=0` (1406 linhas,
  convencao legada deste Arpa = ativo) mapeia certinho para
  `payload.ativo=true` (1406), e `ativo=1` (187) para `false` (187). A
  logica esta correta - foi so uma amostra nao representativa (os 3
  primeiros calharam de ser produtos realmente inativos). Fica registrado
  para nao repetir o susto.
- **Desvio de seguranca temporario, ainda nao revogado em 2026-09-11**:
  para a investigacao acima, foi aplicado `GRANT USAGE ON SCHEMA public TO
  ararasuite_sync_ro; GRANT SELECT ON ALL TABLES IN SCHEMA public TO
  ararasuite_sync_ro;` no Anapolis - mais amplo que a politica de
  `docs/arpa-readonly-security-policy.md` (que pede `SELECT` so nas views
  `sync_export`, nao no `public` inteiro). Ver registro e recomendacao de
  revogar em `docs/arpa-readonly-security-policy.md`.

## NO-GO

Nao prosseguir se:

- o usuario read-only tiver qualquer permissao de escrita;
- o preflight das views falhar;
- `sync_export.produtos`/`.clientes` cairem no timestamp fixo (deveriam usar as
  tabelas de log `alterados`/`alterados_clientes` — conferir na definicao da view);
- houver dead-letter apos a coleta local;
- houver duvida sobre aplicar em `anapolis`.
- a senha DBA precisar ficar salva em arquivo ou no `appsettings.json`.
