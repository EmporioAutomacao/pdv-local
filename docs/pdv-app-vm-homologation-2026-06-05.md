# Homologacao do PDV App na VM - 2026-06-05

## Ambiente

| Item | Valor |
| --- | --- |
| VM | `192.168.0.184` |
| Instalacao | `C:\Program Files\PDVLocal` |
| PDV App | `C:\Program Files\PDVLocal\PDVApp\PdvLocal.App.exe` |
| SyncAgent | `PDV Local Sync Agent` |
| API local | `http://127.0.0.1:47891` |
| Banco local | `pdv_sync` |

## Objetivo

Validar a instalacao parcial do PDV App na VM sem reinstalar o SyncAgent inteiro,
para preservar a ativacao ja existente contra o ERP Docker.

## Acoes executadas

1. Copiado o payload `payload\PDVApp` do pacote gerado para a VM.
2. Instalado o PDV App em `C:\Program Files\PDVLocal\PDVApp`.
3. Criado `appsettings.json` do PDV App apontando para:
   - `Host=localhost;Port=5432;Database=pdv_sync;Username=pdv_sync;Password=pdv_sync`;
   - `http://127.0.0.1:47891`.
4. Criado atalho na area de trabalho publica:
   - `C:\Users\Public\Desktop\PDV Local.lnk`.
5. Criado atalho no Menu Iniciar:
   - `C:\ProgramData\Microsoft\Windows\Start Menu\Programs\PDV Local\PDV Local.lnk`.
6. Validado que os atalhos apontam para `PdvLocal.App.exe`.
7. Iniciado o processo `PdvLocal.App.exe` via WinRM por 3 segundos.

## Evidencias

Validacao tecnica:

```text
ServiceStatus: Running
PdvAppExists: True
PdvConfigExists: True
PdvConfigLocalApi: http://127.0.0.1:47891
PdvConfigConnectionString: Host=localhost;Port=5432;Database=pdv_sync;Username=pdv_sync;Password=pdv_sync
DesktopShortcutExists: True
DesktopShortcutTarget: C:\Program Files\PDVLocal\PDVApp\PdvLocal.App.exe
StartShortcutExists: True
StartShortcutTarget: C:\Program Files\PDVLocal\PDVApp\PdvLocal.App.exe
LocalStatus: ok
RuntimeStatus: idle
Provisioned: True
Pending: 0
DeadLetter: 0
Database timezone: America/Sao_Paulo
pdv tables: 8
sync_agent tables: 6
operators: 0
products: 0
sales: 0
open_cash_sessions: 0
```

Smoke de processo:

```text
StartedProcessId: 4800
WasRunningAfter3Seconds: True
ProcessCountBefore: 0
ProcessCountAfter: 1
```

## Limite da validacao

A execucao via WinRM acontece em sessao nao interativa. Por isso, ela valida que
o executavel inicia, mas nao valida exibicao visual no desktop do usuario.

Validacao visual ainda necessaria:

1. Entrar na VM por console/RDP.
2. Confirmar o icone `PDV Local` na area de trabalho.
3. Abrir o app pelo atalho.
4. Confirmar que a tela exibe:
   - ativacao `Ativado`;
   - runtime `idle`;
   - pendentes `0`;
   - dead-letter `0`;
   - schema PDV `ok / 8 tabelas`;
   - API local `http://127.0.0.1:47891`.

## Resultado

Homologacao tecnica da instalacao parcial aprovada.

Pendencia restante: validacao visual em sessao interativa da VM.

Atualizacao posterior:

- PDV App atualizado na VM em 2026-06-05 para exibir contadores operacionais do
  schema `pdv`.
- Fluxo de criacao manual de operador local nao foi mantido; operadores devem
  ser importados do ERP.
- Contadores validados no banco local:
  - `operators=0`;
  - `products=0`;
  - `sales=0`;
  - `open_cash_sessions=0`.

## Importacao de operadores

Atualizacao aplicada na VM `192.168.0.184` em 2026-06-05:

- SyncAgent atualizado com o cliente `ErpPdvSnapshot`;
- `appsettings.json` preservado a partir do backup da VM e acrescido de:
  - `ErpPdvSnapshot.Enabled=true`;
  - `ErpPdvSnapshot.TimeoutSeconds=120`;
  - `ErpPdvSnapshot.Limit=1000`;
- schema `pdv.operators` atualizado com colunas aditivas:
  - `external_operator_id`;
  - `permissions`;
- ERP cliente Docker em `192.168.0.31:8002` reconstruido para carregar o endpoint
  `GET /v1/sync/pdv/operators:snapshot`.

Evidencia apos `POST http://127.0.0.1:47891/sync-now`:

```text
RuntimeStatus: idle
Pending: 0
DeadLetter: 0
OperatorCount: 28
LogsStatus: 200
HasOperatorSnapshotLog: True
HasSnapshotSucceeded: True
HasSnapshotHttp200: True
```

Preview de operadores importados:

```text
admin:admin:true
arpa_21:operator:true
arpa_37:operator:true
Daniela:supervisor:true
dbg:supervisor:true
```

Observacao operacional:

- Durante a primeira tentativa de atualizacao, o `appsettings.json` instalado foi
  sobrescrito pelo payload padrao. A configuracao correta foi restaurada do
  backup `C:\ProgramData\PDVLocal\backups\SyncAgent-bin-20260605-005255`.
- Em atualizacoes futuras, preservar sempre o `appsettings.json` instalado e
  copiar apenas binarios ou aplicar merge controlado de novas secoes.

## Atualizacao do PDV Core

Atualizacao aplicada na VM em 2026-06-05:

- PDV App atualizado com a biblioteca `PdvLocal.Core.dll`;
- `appsettings.json` do PDV App preservado a partir do backup local;
- backup criado em
  `C:\ProgramData\PDVLocal\backups\PDVApp-bin-20260605-075518`.

Evidencia:

```text
PdvAppExists: True
PdvCoreExists: True
ConfigExists: True
```

## Atualizacao do fluxo de caixa/venda

Atualizacao aplicada na VM em 2026-06-05:

- PDV App atualizado com painel operacional inicial;
- fluxo disponivel na UI:
  - carregar operador por login;
  - abrir caixa;
  - gravar venda simples de um item;
  - fechar caixa;
- `appsettings.json` do PDV App preservado;
- backup criado em
  `C:\ProgramData\PDVLocal\backups\PDVApp-bin-20260605-080601`.

Smoke via WinRM:

```text
PdvAppExists: True
PdvCoreExists: True
WasRunningAfter3Seconds: True
ProcessCountBefore: 0
ProcessCountAfter: 1
```

Validacao visual ainda pendente:

1. Abrir o atalho `PDV Local` na VM por console/RDP.
2. Carregar operador importado, por exemplo `admin`.
3. Abrir caixa.
4. Finalizar uma venda simples.
5. Confirmar incremento em `Vendas locais`.
6. Fechar caixa.

## Homologacao de venda PDV para ERP

Validacao executada em 2026-06-05:

- ERP Docker integrado ativo em `http://192.168.0.31:8002`.
- Migration ERP `vendas.0021_alter_venda_origem` aplicada.
- SyncAgent atualizado na VM `192.168.0.184`, preservando `appsettings.json`.
- Constraint local `sync_agent.outbox_events.source_system` ajustada para
  aceitar `arpa` e `pdv_local`.
- Venda tecnica criada no PostgreSQL local:
  - `sale_id=70000000-0000-4000-8000-000000000003`;
  - `sale_number=PDV-HOMOLOG-0001`;
  - `sync_status=pending_sync`.
- Sincronizacao manual disparada via `POST http://127.0.0.1:47891/sync-now`.
- Resultado local:
  - `pdv.sales.sync_status=accepted`;
  - `sync_agent.outbox_events.source_system=pdv_local`;
  - `sync_agent.outbox_events.entity_type=venda`;
  - `sync_agent.outbox_events.status=accepted`;
  - `dead_letter_events=0`.
- Resultado ERP:
  - `vendas_venda.origem=pdv_local`;
  - `codigo_externo=70000000-0000-4000-8000-000000000003`;
  - `status=faturada`;
  - `subtotal=20.00`;
  - `desconto_total=0.00`;
  - `total=19.00`;
  - 1 item criado em `vendas_vendaitem`.
- Idempotencia:
  - nova chamada em `POST http://127.0.0.1:47891/sync-now`;
  - venda local permaneceu `accepted`;
  - `sync_agent.outbox_events` manteve 1 evento para o `sale_id`;
  - ERP manteve 1 venda para `origem=pdv_local` + `codigo_externo`.

Observacao: o desconto total do ERP ficou `0.00` porque o desconto da venda
tecnica foi enviado no item. O total final foi preservado em `19.00`.

## Dashboard de vendas PDV

Atualizacao aplicada na VM em 2026-06-05:

- `GET http://127.0.0.1:47891/status` passou a retornar
  `pdv_sales_summary`.
- Dashboard local `GET http://127.0.0.1:47891/` passou a exibir cards:
  - `Vendas PDV pendentes`;
  - `Vendas PDV enviadas`;
  - `Vendas PDV aceitas`;
  - `Vendas PDV rejeitadas`.
- `GET http://127.0.0.1:47891/help` explica o indicador
  `pdv_sales_summary`.

Evidencia na VM:

```json
{"status":"ok","pdv_sales_summary":{"accepted":3},"pending_outbox_events":0,"dead_letter_events":0}
```

Smoke HTML:

```text
StatusCode: 200
HasPendingSalesCard: true
HasSentSalesCard: true
HasAcceptedSalesCard: true
HasRejectedSalesCard: true
```

## Reprocessamento de venda rejeitada

Atualizacao aplicada na VM em 2026-06-05:

- Dashboard local exibe tabela de vendas `rejected` quando houver registros.
- Cada linha possui botao `Reprocessar`.
- Endpoint operacional:
  - `POST http://127.0.0.1:47891/pdv-sales/reprocess`;
  - body `application/x-www-form-urlencoded`: `sale_id=<uuid>`.
- A rotina:
  - remove `sync_agent.dead_letter_events` para o evento;
  - muda `sync_agent.outbox_events.status` para `pending`;
  - limpa `last_error`;
  - muda `pdv.sales.sync_status` para `sent`;
  - sinaliza ciclo manual do Worker.

Evidencia de homologacao:

```json
{
  "DashboardHadReprocessFormBefore": true,
  "Reprocess": {
    "requeued": true,
    "sale_id": "70000000-0000-4000-8000-000000000003",
    "message": "Venda reenfileirada para reprocessamento."
  },
  "PdvSalesSummary": {
    "accepted": 3
  },
  "Db": [
    "accepted|accepted|",
    "0"
  ]
}
```

Leitura da evidencia:

- antes do POST, o dashboard tinha formulario de reprocessamento;
- apos o POST e o ciclo manual, `pdv.sales.sync_status=accepted`;
- `sync_agent.outbox_events.status=accepted`;
- nao ficou registro em `sync_agent.dead_letter_events`.

## Matriz de integracao PDV-Sync-ERP

Validacao complementar executada em 2026-06-05 usando a skill
`sync-integration-test-matrix`.

### Payload invalido

Cenario:

- entidade: `venda`;
- operacao: `create`;
- rede: online;
- qualidade: payload invalido;
- idempotencia: primeiro envio;
- venda tecnica: `PDV-MATRIX-INVALID-0001`;
- `sale_id=70000000-0000-4000-8000-000000000106`;
- payload gerado sem itens (`items=[]`).

Resultado esperado:

- ERP rejeita a venda;
- venda local vira `rejected`;
- outbox vai para `dead_letter`;
- motivo fica persistido para suporte.

Evidencia:

```json
{
  "PdvSalesSummary": {
    "accepted": 3,
    "rejected": 1
  },
  "PendingOutbox": 0,
  "DeadLetter": 1,
  "Db": [
    "rejected|dead_letter|pdv_local.venda.items must be a non-empty list",
    "pdv_local.venda.items must be a non-empty list"
  ]
}
```

Após registrar a evidencia, os artefatos dessa venda invalida foram removidos
da VM para nao deixar dead-letter operacional residual.

### ERP offline e recuperacao

Cenario:

- entidade: `venda`;
- operacao: `create`;
- rede: offline-recovery;
- qualidade: payload valido;
- idempotencia: primeiro envio com retry;
- ERP web parado temporariamente:
  `arara-integrated-dev-erp_cliente_web-1`;
- venda tecnica: `PDV-MATRIX-OFFLINE-0001`;
- `sale_id=70000000-0000-4000-8000-000000000206`.

Resultado esperado durante offline:

- venda local sai de `pending_sync` para `sent`;
- outbox fica `pending`;
- `last_error=HttpRequestException`;
- nao gera dead-letter.

Evidencia durante offline:

```json
{
  "runtime_status": "running",
  "last_error": "HttpRequestException",
  "pending_outbox_events": 1,
  "dead_letter_events": 1,
  "pdv_sales_summary": {
    "sent": 1,
    "accepted": 3,
    "rejected": 1
  },
  "Db": "sent|pending|HttpRequestException"
}
```

Observacao: o `dead_letter_events=1` nesse momento era da venda invalida da
matriz anterior, nao da venda offline.

Resultado esperado apos recuperar ERP:

- ERP web iniciado novamente;
- primeira tentativa respeitou backoff do erro transitorio;
- apos a janela de retry, sincronizacao manual aceitou a venda;
- venda local virou `accepted`;
- outbox virou `accepted`;
- pendentes zeraram.

Evidencia apos recuperacao:

```json
{
  "RuntimeStatus": "idle",
  "LastError": null,
  "PdvSalesSummary": {
    "accepted": 4,
    "rejected": 1
  },
  "PendingOutbox": 0,
  "DeadLetter": 1,
  "Db": "accepted|accepted|"
}
```

Confirmacao no ERP:

```text
1|faturada|30.00
```

Estado final da VM apos limpeza da venda invalida:

```json
{
  "PdvSalesSummary": {
    "accepted": 4
  },
  "PendingOutbox": 0,
  "DeadLetter": 0,
  "LastError": null
}
```

## Validacao do PDV App instalado

Validacao executada em 2026-06-05 na VM `192.168.0.184`.

Escopo validado por automacao remota:

- executavel instalado em `C:\Program Files\PDVLocal\PDVApp\PdvLocal.App.exe`;
- atalho publico criado em `C:\Users\Public\Desktop\PDV Local.lnk`;
- atalho aponta para o executavel correto;
- `WorkingDirectory` do atalho aponta para `C:\Program Files\PDVLocal\PDVApp`;
- SyncAgent local responde `GET http://127.0.0.1:47891/status` com `status=ok`;
- base local tem operadores, produtos e vendas para teste operacional;
- o executavel do PDV App inicia e permanece em execucao por pelo menos 5 segundos,
  sem erro imediato de runtime.

Evidencia do atalho:

```json
{
  "Shortcut": "C:\\Users\\Public\\Desktop\\PDV Local.lnk",
  "TargetPath": "C:\\Program Files\\PDVLocal\\PDVApp\\PdvLocal.App.exe",
  "Arguments": "",
  "WorkingDirectory": "C:\\Program Files\\PDVLocal\\PDVApp",
  "Exists": true
}
```

Evidencia do smoke test:

```json
{
  "exe_exists": true,
  "started": true,
  "exited": false,
  "exit_code": "still_running_after_5s",
  "error": null
}
```

Evidencia da base local:

```text
operators=28
active_operators=28
products=3
sales=4
open_cash_sessions=2
```

Limite da validacao remota:

- WinRM nao controla a sessao grafica interativa do usuario.
- O clique real na tela do PDV App deve ser feito via console/RDP da VM.
- Apos um teste manual de venda pela tela, a confirmacao tecnica deve verificar
  `pdv.sales.sync_status=accepted` e a venda criada no ERP com `origem=pdv_local`.

## Validacao manual pela tela do PDV App

Validacao executada em 2026-06-05 com o PDV App aberto na sessao grafica da VM
`192.168.0.184`, usuario `suporte`.

Venda criada pela tela:

```text
sale_id=44dde029-510e-46ed-b047-03ae740e99c3
sale_number=PDV-20260605150230
total=10.00
```

Evidencia local apos sincronizacao:

```text
44dde029-510e-46ed-b047-03ae740e99c3|PDV-20260605150230|accepted|10.00|2026-06-05 15:02:30.258811-03
outbox=5
pending=0
dead_letter=0
```

Evidencia do SyncAgent:

```json
{
  "status": "ok",
  "pending_outbox_events": 0,
  "dead_letter_events": 0,
  "pdv_sales_summary": {
    "accepted": 5
  },
  "runtime_status": "idle",
  "last_error": null
}
```

Confirmacao no ERP:

```text
6460|44dde029-510e-46ed-b047-03ae740e99c3|pdv_local|faturada|10.00|2026-06-05 15:02:30.260174-03
itens=1
vendas_pdv_local=5
```

Resultado:

- PDV App gravou venda local;
- SyncAgent capturou e enviou a venda;
- ERP aceitou a venda;
- venda ficou `accepted` no PDV local;
- venda ficou `faturada` no ERP;
- nao restaram pendencias nem dead-letter.

## Atualizacao para frente de caixa

Atualizacao executada em 2026-06-05 para iniciar a Fase 7 do PDV Local.

Escopo implantado:

- nova tela de frente de caixa no `PdvLocal.App`;
- bloqueio de venda sem operador carregado e caixa aberto;
- busca de produto por ID, SKU/codigo externo, codigo de barras ou nome;
- carrinho com multiplos itens;
- desconto por item e desconto total;
- multiplas especies de pagamento;
- calculo de troco para dinheiro;
- especies TEF simuladas para cartao, sem pinpad/provedor real;
- finalizacao preservando o fluxo `pdv.sales.sync_status=pending_sync`.

Validacoes locais antes da copia para VM:

```text
dotnet build D:\GitHub\pdv-local\pdv-local.sln
Resultado: 0 avisos, 0 erros

dotnet test D:\GitHub\pdv-local\pdv-local.sln
Resultado: 14 testes aprovados
```

Smoke local do executavel:

```json
{
  "exe_exists": true,
  "started": true,
  "exited": false,
  "exit_code": "still_running_after_5s",
  "error": null
}
```

Implantacao na VM:

```text
BackupDir=C:\ProgramData\PDVLocal\backups\PDVApp-front-cashier-20260605-161919
AppSettingsBackup=C:\ProgramData\PDVLocal\backups\PDVApp-appsettings-20260605-161919.json
Destino=C:\Program Files\PDVLocal\PDVApp
```

Evidencia de abertura na sessao grafica:

```json
{
  "ProcessName": "PdvLocal.App",
  "SessionId": 2
}
```

Pendencia de homologacao desta entrega:

- executar venda real pela nova tela com multiplos itens e pagamento misto;
- confirmar `pdv.sales.sync_status=accepted`;
- confirmar chegada no ERP com `origem=pdv_local`.

## Atualizacao do catalogo de produtos

Atualizacao executada em 2026-06-05 para concluir a sincronizacao inicial do
catalogo ERP -> PDV usada pela frente de caixa.

Escopo implantado:

- ERP exposto pelo contrato `sync` `1.7.0`:
  `GET /v1/sync/pdv/products:snapshot`;
- SyncAgent consumindo snapshot de produtos quando `ErpPdvSnapshot.Enabled=true`;
- limite do snapshot ajustado para `5000` produtos por chamada;
- produtos gravados em `pdv.products` com origem `source_system=erp`;
- watermark salvo em `sync_agent.agent_state` com chave
  `pdv.products.last_snapshot_at_utc`.

Implantacao na VM:

```text
BackupDir=C:\ProgramData\PDVLocal\backups\SyncAgent-products-snapshot-limit5000-20260605-185526
Servico=PDV Local Sync Agent
Status=Running
ErpPdvSnapshot.Limit=5000
```

Validacao via API local:

```text
POST http://127.0.0.1:47891/sync-now
GET  http://127.0.0.1:47891/status

runtime_status=idle
last_error=
last_cycle_completed_at_utc=2026-06-05T21:56:07.3863285+00:00
```

Validacao no PostgreSQL local da VM:

```text
products=1568
erp_products=1565
active_erp_products=1388
product_snapshot_state={"error": null, "imported": 0, "succeeded": true, "timestamp_utc": "2026-06-05T21:57:07.7940132+00:00", "response_status_code": 200}
product_snapshot_last_at={"value": "2026-06-05T21:57:07.7940132+00:00"}
```

Observacao: o ultimo ciclo registrou `imported=0` porque os produtos ja estavam
atualizados no banco local. A contagem consolidada confirmou `1565` produtos do
ERP disponiveis para o PDV.

## Atualizacao de especies e condicoes de pagamento

Atualizacao executada em 2026-06-05 para concluir a sincronizacao inicial dos
cadastros de pagamento ERP -> PDV.

Escopo implantado:

- ERP exposto pelo contrato `sync` `1.8.0`:
  `GET /v1/sync/pdv/payment-methods:snapshot`;
- SyncAgent consumindo snapshot de pagamentos quando
  `ErpPdvSnapshot.Enabled=true`;
- especies gravadas em `pdv.payment_species`;
- condicoes gravadas em `pdv.payment_conditions`;
- watermark salvo em `sync_agent.agent_state` com chave
  `pdv.payment_methods.last_snapshot_at_utc`;
- logs do dashboard local incluem a tarefa `pdv_payment_methods_snapshot`.

Implantacao na VM:

```text
BackupDir=C:\ProgramData\PDVLocal\backups\SyncAgent-payment-methods-snapshot-20260605-195629
Servico=PDV Local Sync Agent
Status=Running
```

Validacao via API local:

```text
POST http://127.0.0.1:47891/sync-now
GET  http://127.0.0.1:47891/status

runtime_status=idle
last_error=
last_cycle_completed_at_utc=2026-06-05T22:58:51.0454505+00:00
```

Validacao no PostgreSQL local da VM:

```text
payment_species=52
active_payment_species=52
payment_conditions=78
active_payment_conditions=78
payment_methods_snapshot_state={"error": null, "imported": 130, "succeeded": true, "timestamp_utc": "2026-06-05T22:58:51.0228438+00:00", "response_status_code": 200}
payment_methods_snapshot_last_at={"value": "2026-06-05T22:58:51.0228438+00:00"}
```

Amostra operacional validada:

```text
dinheiro:cash:tef=false:troco=true
Dinheiro:cash:tef=false:troco=true
Pix:pix:tef=false:troco=false
CARTAO CREDITO:card:tef=true:troco=false
```

## Atualizacao da UI de pagamento

Atualizacao executada em 2026-06-05 para fazer o PDV App usar os cadastros de
pagamento sincronizados do ERP.

Escopo implantado:

- combo de especie carregado de `pdv.payment_species`;
- combo de condicao carregado de `pdv.payment_conditions`;
- fallback local mantido apenas para ambiente sem snapshot;
- pagamento local grava metadados no `payload` de `pdv.payments`;
- opcoes fixas de especie foram removidas do XAML da tela.

Validacoes locais antes da copia para VM:

```text
dotnet build D:\GitHub\pdv-local\pdv-local.sln
Resultado: 0 avisos, 0 erros

dotnet test D:\GitHub\pdv-local\pdv-local.sln
Resultado: 15 testes aprovados
```

Implantacao na VM:

```text
BackupDir=C:\ProgramData\PDVLocal\backups\PDVApp-payment-catalog-ui-20260605-201227
Destino=C:\Program Files\PDVLocal\PDVApp
AppSettingsPreserved=True
SyncAgentServiceStatus=Running
```

Validacao de dados disponiveis para a tela:

```text
species=52
conditions=78
```

Evidencia de abertura na sessao grafica:

```text
ProcessName=PdvLocal.App
SessionId=2
StartTime=05/06/2026 20:13:12
```

Pendencia de homologacao desta entrega:

- executar uma venda real pela nova tela usando produto sincronizado, especie
  sincronizada, condicao sincronizada e pagamento misto;
- confirmar gravacao do `payload` em `pdv.payments`;
- confirmar aceite da venda no ERP.

## Homologacao tecnica de venda multi-item e pagamento misto

Atualizacao executada em 2026-06-05 para validar o fluxo completo PDV -> ERP
com produtos e pagamentos sincronizados.

Executor tecnico:

```text
Projeto=src\pdv-homologation\PdvLocal.Homologation.csproj
Destino VM=C:\ProgramData\PDVLocal\Homologation\mixed-payment-sale
```

Primeira venda criada:

```text
sale_id=6e85137f-b292-4e1d-8aba-12ca89df97bf
sale_number=PDV-HML-20260605205705
total=601.32
produtos=1187|2098, 2099, 9098 6102: PAINEL TECLADO ; 1234|2098PP.LC: PCI PRINCIPAL
pagamentos=dinheiro 300.66 + Pix 300.66
```

Resultado da primeira venda:

- PDV local gravou pagamentos com payload de especie/condicao corretamente;
- venda foi aceita pelo SyncAgent;
- ERP recebeu a venda, mas associou itens por `codigo_arpa` quando
  `product_external_key` era numerico;
- bug confirmado: `product_external_key=1187/1234` era ID do ERP vindo do
  snapshot, mas o ERP interpretava como `codigo_arpa`.

Correcao aplicada:

- contrato `sync` atualizado para `1.8.1`;
- schema `PdvSalePayloadV1` recebeu `items[].product_erp_id` opcional;
- exemplo oficial de venda PDV atualizado;
- SyncAgent passou a enviar `product_erp_id` nos itens quando o produto veio do
  ERP;
- ERP passou a preferir `product_erp_id` antes de `product_external_key`;
- testes ERP `sync_api` passaram com 53 testes;
- build/testes PDV Local passaram com 0 erros e 15 testes.

Implantacao da correcao:

```text
ERP Docker integrado reconstruido
Backup SyncAgent=C:\ProgramData\PDVLocal\backups\SyncAgent-product-erp-id-fix-20260605-210210
Servico=PDV Local Sync Agent
Status=Running
```

Venda corrigida:

```text
sale_id=567aa591-495a-4a9f-b04a-b5e03d5fc8d9
sale_number=PDV-HML-20260605210240
total=601.32
produtos=1187|2098, 2099, 9098 6102: PAINEL TECLADO ; 1234|2098PP.LC: PCI PRINCIPAL
pagamentos=dinheiro 300.66 + Pix 300.66
```

Validacao no PDV local:

```text
sale=PDV-HML-20260605210240|accepted|601.32
event=accepted|1187|1234
dead_letter=0
```

Validacao no ERP:

```text
venda=6463|567aa591-495a-4a9f-b04a-b5e03d5fc8d9|pdv_local|faturada|601.32|dinheiro|None
itens=2
produtos=1187:2098, 2099, 9098 6102: PAINEL TECLADO:1.0000:84.09:84.09|1234:2098PP.LC: PCI PRINCIPAL:1.0000:517.23:517.23
```

Conclusao:

- fluxo tecnico PDV -> SyncAgent -> ERP aprovado para venda multi-item e
  pagamento misto;
- homologacao visual por clique na UI WPF deve ser mantida como evidencia
  operacional separada quando houver mudanca relevante na tela.

## Homologacao visual automatizada pela UI WPF

Validacao executada em 2026-06-06 na VM `192.168.0.184`, usando a sessao
grafica interativa do usuario `Suporte`.

Metodo:

- script temporario em `C:\ProgramData\PDVLocal\Homologation`;
- execucao por `schtasks /IT /RU Suporte`;
- automacao via `UIAutomationClient`, usando os `AutomationId` dos controles
  WPF;
- sem clique por coordenada.

Fluxo executado pela tela:

1. `Nova venda`.
2. Operador `admin`.
3. `Abrir caixa`, reaproveitando caixa aberto existente quando aplicavel.
4. Produto `1187`.
5. Produto `1234`.
6. Pagamento em `dinheiro`, condicao padrao do catalogo.
7. `Finalizar venda`.

Resultado da tela:

```text
sale_id=69d84a8e-8536-476e-aad5-c7e1b8b1b3c7
sale_number=PDV-20260606000010
message=Venda PDV-20260606000010 finalizada e pendente de sincronizacao. ID: 69d84a8e-8536-476e-aad5-c7e1b8b1b3c7.
```

Validacao no PDV local:

```text
sale_number=PDV-20260606000010
sync_status=accepted
total_amount=601.32
outbox.status=accepted
outbox.attempt_count=1
items[0].product_erp_id=1187
items[1].product_erp_id=1234
dead_letter=0
```

Pagamento local gravado:

```text
payment_method=dinheiro
amount=601.32
payment_species_external_key=52
payment_condition_external_key=3
requires_tef=false
allows_change=true
```

Validacao no ERP:

```text
venda=6464|69d84a8e-8536-476e-aad5-c7e1b8b1b3c7|pdv_local|faturada|601.32
itens=2
produto=1187|2098, 2099, 9098 6102: PAINEL TECLADO|1.0000|84.09|84.09
produto=1234|2098PP.LC: PCI PRINCIPAL|1.0000|517.23|517.23
```

Conclusao:

- homologacao visual da frente de caixa aprovada;
- o fluxo da UI WPF gravou a venda no banco local;
- o SyncAgent enviou o evento com `product_erp_id`;
- o ERP associou os itens aos produtos corretos;
- nao restaram pendencias nem dead-letter para a venda.

## Homologacao do contrato de pagamento 1.9.0

Validacao executada em 2026-06-06 para oficializar o envio de especie,
condicao e metadados TEF nao sensiveis no payload de venda PDV.

Escopo implantado:

- contrato `sync` atualizado para `1.9.0`;
- `PdvSalePayloadV1.payments[]` recebeu campos opcionais:
  - `payment_species_external_key`;
  - `payment_species_kind`;
  - `payment_condition_external_key`;
  - `installments`;
  - `requires_tef`;
  - `allows_change`;
  - `tef_metadata`;
- SyncAgent atualizado na VM preservando `appsettings.json`;
- ERP Docker integrado reconstruido.

Implantacao do SyncAgent:

```text
BackupDir=C:\ProgramData\PDVLocal\backups\SyncAgent-payment-metadata-contract-190-20260606-102049
ConfigBackup=C:\ProgramData\PDVLocal\backups\SyncAgent-appsettings-20260606-102057.json
Servico=PDV Local Sync Agent
Status=Running
```

Venda tecnica criada no PDV local:

```text
sale_id=4bf6b5a8-9427-49fc-a17d-9b4b5070bef0
sale_number=PDV-HML-TEF-20260606102217
authorization_code=TEF-HML-20260606102217
total=601.32
payment_species_external_key=16
payment_condition_external_key=14
installments=2
requires_tef=true
```

Validacao no PDV local:

```text
sale=PDV-HML-TEF-20260606102217|accepted|601.32
outbox=accepted|attempt_count=1
payload.payment_species_external_key=16
payload.payment_condition_external_key=14
payload.tef_metadata.authorization_code=TEF-HML-20260606102217
payload.items[0].product_erp_id=1187
payload.items[1].product_erp_id=1234
dead_letter=0
```

Validacao no ERP:

```text
venda=6465|4bf6b5a8-9427-49fc-a17d-9b4b5070bef0|pdv_local|faturada|601.32
especie=16|CARTAO CREDITO
condicao=14|28/56|2 parcelas
evento=applied
tef_metadata.authorization_code=TEF-HML-20260606102217
produto=1187|2098, 2099, 9098 6102: PAINEL TECLADO|1.0000|84.09|84.09
produto=1234|2098PP.LC: PCI PRINCIPAL|1.0000|517.23|517.23
```

Testes executados:

```text
ERP: D:\GitHub\erp\venv\Scripts\python.exe D:\GitHub\erp\manage.py test sync_api --keepdb
Resultado: 54 testes aprovados

PDV: dotnet build D:\GitHub\pdv-local\pdv-local.sln
Resultado: 0 avisos, 0 erros

PDV: dotnet test D:\GitHub\pdv-local\pdv-local.sln
Resultado: 15 testes aprovados
```

Conclusao:

- contrato de pagamento PDV `1.9.0` aprovado;
- SyncAgent envia especie, condicao, parcelas, flags operacionais e TEF metadata;
- ERP associa especie e condicao pelos IDs do snapshot;
- metadados TEF ficam retidos no `SyncReceivedEvent.payload`;
- nao ha armazenamento de PAN, CVV, trilha, senha, nome do portador ou validade
  do cartao.

## Homologacao de autorizacao e auditoria de supervisor

Validacao executada em 2026-06-06 na VM `192.168.0.184`.

Escopo implantado:

- PDV App atualizado com painel `Autorizacao`;
- operadores com papel `admin` ou `supervisor` podem autorizar operacoes
  sensiveis;
- motivo obrigatorio para autorizacao;
- tabela local `pdv.operation_audits`;
- auditoria para:
  - desconto de item;
  - desconto total;
  - remocao de item;
  - remocao de pagamento;
  - limpeza de venda em andamento.

Implantacao do PDV App:

```text
BackupDir=C:\ProgramData\PDVLocal\backups\PDVApp-supervisor-audit-20260606-111926
ConfigBackup=C:\ProgramData\PDVLocal\backups\PDVApp-appsettings-20260606-111928.json
ProcessName=PdvLocal.App
SessionId=2
```

Schema local:

```text
pdv.operation_audits existe
audit_count inicial=0
```

Teste negativo pela UI:

```text
operador=admin
produto=1187
desconto_item=1.00
supervisor=nao informado
resultado=Autorizacao de supervisor obrigatoria para aplicar desconto no item. Informe o login e clique Autorizar.
```

Teste positivo pela UI:

```text
sale_id=ad51f563-4080-4451-843b-b197e65bceae
sale_number=PDV-20260606112301
supervisor=admin
motivo=Homologacao de desconto e auditoria supervisor
subtotal=601.32
desconto_item=1.00
desconto_total=2.00
desconto_consolidado=3.00
total=598.32
```

Auditoria local:

```text
item_discount_applied|operator=admin|supervisor=admin|reason=Homologacao de desconto e auditoria supervisor
sale_discount_applied|operator=admin|supervisor=admin|reason=Homologacao de desconto e auditoria supervisor
```

Validacao local apos SyncAgent:

```text
sale=PDV-20260606112301|accepted|subtotal=601.32|discount=3.00|total=598.32
outbox=accepted|attempt_count=1
payload.items[0].discount_amount=1.00
payload.discount_amount=3.00
payload.total_amount=598.32
dead_letter=0
```

Validacao no ERP:

```text
venda=6466|ad51f563-4080-4451-843b-b197e65bceae|pdv_local|faturada
subtotal=601.32
desconto_total=2.00
total=598.32
item_produto=1187|desconto=1.00|total_liquido=83.09
item_produto=1234|desconto=0.00|total_liquido=517.23
evento=applied
```

Testes executados:

```text
dotnet build D:\GitHub\pdv-local\pdv-local.sln
Resultado: 0 avisos, 0 erros

dotnet test D:\GitHub\pdv-local\pdv-local.sln
Resultado: 18 testes aprovados
```

Conclusao:

- desconto sem supervisor foi bloqueado;
- desconto de item e desconto total com supervisor foram aceitos;
- auditoria local ficou vinculada ao `sale_id`;
- venda sincronizou e foi aplicada no ERP;
- cancelamento de venda ja finalizada ainda nao possui tela propria e deve ser
  tratado junto com fiscal/TEF quando houver estorno.

## Homologacao de suprimento, sangria e resumo de caixa

Data: 2026-06-06.

Escopo entregue:

- tabela local `pdv.cash_movements`;
- suprimento e sangria pela UI WPF;
- autorizacao obrigatoria por operador `admin`/`supervisor`;
- motivo obrigatorio;
- auditoria local em `pdv.operation_audits`;
- bloqueio de sangria maior que o dinheiro esperado no caixa;
- resumo local com abertura, vendas, dinheiro em vendas, suprimentos, sangrias,
  dinheiro esperado e vendas por especie.

Implantacao na VM:

```text
VM=192.168.0.184
InstallDir=C:\Program Files\PDVLocal\PDVApp
Artifact=D:\GitHub\pdv-local\artifacts\pdv-app\cash-movements
BackupDir=C:\ProgramData\PDVLocal\backups\PDVApp-cash-movements-20260606-114725
ConfigBackup=C:\ProgramData\PDVLocal\backups\PDVApp-appsettings-20260606-114725.json
Process=PdvLocal.App
SessionId=2
```

Smoke de bloqueio:

```text
Motivo inicial=Homologacao caixa 20260606114954
Resultado=movimento bloqueado porque a autorizacao foi limpa ao clicar Nova venda
Mensagem=Autorizacao de supervisor obrigatoria para registrar sangria. Informe o login e clique Autorizar.
```

Smoke aprovado:

```text
Motivo=Homologacao caixa 20260606115102
Operador=admin (admin / admin)
Caixa=70000000-0000-4000-8000-000000000101 / open
Suprimento=10.00
Sangria=3.00
Dinheiro esperado no resumo=2668.18
Mensagem final=Sangria registrado com auditoria. ID: 168583e0-778d-4999-99ff-06cfa3bf2b65.
```

Validacao local no PostgreSQL:

```text
movement|supply|10.00|Homologacao caixa 20260606115102|509a5f4c-3701-48db-939f-fa74dbc6f6fd
movement|withdrawal|3.00|Homologacao caixa 20260606115102|168583e0-778d-4999-99ff-06cfa3bf2b65
audit|cash_supply_recorded|Homologacao caixa 20260606115102|{"amount": 10.00, "movement_id": "509a5f4c-3701-48db-939f-fa74dbc6f6fd", "movement_type": "supply"}
audit|cash_withdrawal_recorded|Homologacao caixa 20260606115102|{"amount": 3.00, "movement_id": "168583e0-778d-4999-99ff-06cfa3bf2b65", "movement_type": "withdrawal"}
totals|10.00|3.00
```

Testes executados:

```text
dotnet build D:\GitHub\pdv-local\pdv-local.sln
Resultado: 0 avisos, 0 erros

dotnet test D:\GitHub\pdv-local\pdv-local.sln
Resultado: 23 testes aprovados
```

Conclusao:

- suprimento e sangria foram gravados no banco local;
- os dois movimentos geraram auditoria local;
- o resumo do caixa refletiu os movimentos;
- esta entrega nao altera contrato `sync`, pois os movimentos ainda sao
  operacionais locais.

## Homologacao da camada TEF configuravel

Data: 2026-06-06.

Escopo entregue:

- fronteira local `ITefPaymentProvider`;
- provider configuravel por `Tef:Mode`;
- modos `Simulated`, `Disabled` e `Provider`;
- modo `Provider` falha fechado enquanto nao houver adaptador real instalado;
- validacao no core contra chaves sensiveis em `tef_metadata`;
- gravacao de `pdv.payments.payload.tef_metadata`;
- envio ao ERP pelo contrato `sync` `1.9.0`.

Implantacao na VM:

```text
VM=192.168.0.184
InstallDir=C:\Program Files\PDVLocal\PDVApp
Artifact=D:\GitHub\pdv-local\artifacts\pdv-app\tef-provider-boundary
BackupDir=C:\ProgramData\PDVLocal\backups\PDVApp-tef-provider-boundary-20260606-121321
ConfigBackup=C:\ProgramData\PDVLocal\backups\PDVApp-appsettings-20260606-121321.json
Process=PdvLocal.App
SessionId=2
```

Homologacao tecnica:

```text
sale_number=PDV-HML-TEF-20260606121502
sale_id=ab0f75ca-cbfd-46e1-a1b2-47c29b866224
total=601.32
payment=dinheiro|300.66
payment=CARTAO CREDITO|300.66
authorization_code=TEF-HML-20260606121502
tef_metadata.authorization_code=HML902711
tef_metadata.provider=homologation
tef_metadata.transaction_id=TEF-HML-20260606151502711
```

Validacao local:

```text
sale|PDV-HML-TEF-20260606121502|ab0f75ca-cbfd-46e1-a1b2-47c29b866224|accepted|601.32
payment|dinheiro|300.66|-|-|-|-|-
payment|CARTAO CREDITO|300.66|TEF-HML-20260606121502|HML902711|true|homologation|TEF-HML-20260606151502711
outbox|accepted|1|HML902711|homologation|TEF-HML-20260606151502711
dead_letter|0
```

Validacao no ERP:

```text
event|applied|ab0f75ca-cbfd-46e1-a1b2-47c29b866224|HML902711|homologation
venda|6467|ab0f75ca-cbfd-46e1-a1b2-47c29b866224|pdv_local|faturada|601.32
```

Testes executados:

```text
dotnet build D:\GitHub\pdv-local\pdv-local.sln
Resultado: 0 avisos, 0 erros

dotnet test D:\GitHub\pdv-local\pdv-local.sln
Resultado: 25 testes aprovados
```

Conclusao:

- a simulacao TEF saiu do clique de pagamento e passou para provider dedicado;
- metadados TEF nao sensiveis foram persistidos e sincronizados;
- o ERP recebeu o evento e manteve `tef_metadata` no payload recebido;
- a integracao TEF real ainda depende da escolha do fornecedor e do adaptador
  certificado.
