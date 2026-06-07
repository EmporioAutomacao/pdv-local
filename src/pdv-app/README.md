# PDV App

Aplicacao WPF do PDV Local.

## Status atual

Fundacao inicial criada em .NET 8:

- tela principal WPF;
- leitura de `GET http://127.0.0.1:47891/status`;
- leitura do PostgreSQL local configurado em `ConnectionStrings:PdvLocalDb`;
- configuracao centralizada em `appsettings.json`;
- exibicao de ativacao, runtime, heartbeat, pendencias, dead-letter, tenant,
  ERP API, API local do SyncAgent, banco local, pgvector, schema `pdv`,
  operadores, produtos, vendas e caixas abertos;
- atalho para `http://127.0.0.1:47891/setup`.
- camada de dados extraida para `src/pdv-core`, compartilhando leitura de status
  do banco e repositórios iniciais de operador, caixa e venda.
- frente de caixa inicial para carregar operador, abrir/fechar caixa, buscar
  produtos, montar carrinho, aplicar desconto e gravar venda local com multiplos
  pagamentos.

## Configuracao

Arquivo padrao:

```json
{
  "ConnectionStrings": {
    "PdvLocalDb": "Host=localhost;Port=5432;Database=pdv_sync;Username=pdv_sync;Password=pdv_sync"
  },
  "SyncAgent": {
    "LocalApiBaseUrl": "http://127.0.0.1:47891"
  }
}
```

Overrides por variavel de ambiente:

| Variavel | Uso |
| --- | --- |
| `PDV_LOCAL_CONNECTION_STRING` | Sobrescreve `ConnectionStrings:PdvLocalDb`. |
| `PDV_LOCAL_SYNC_AGENT_URL` | Sobrescreve `SyncAgent:LocalApiBaseUrl`. |

## Execucao local

```powershell
dotnet run --project D:\GitHub\pdv-local\src\pdv-app\PdvLocal.App.csproj
```

## Fluxo operacional inicial

1. Confirme que o SyncAgent importou operadores do ERP.
2. Informe o login do operador importado.
3. Clique `Carregar`.
4. Informe o valor de abertura e clique `Abrir caixa`.
5. Lance produtos pelo campo principal usando ID, SKU/codigo externo, codigo de
   barras ou nome.
6. Se a busca retornar mais de um produto, de duplo clique no produto correto.
7. Informe quantidade e, quando necessario, desconto do item.
8. Clique `Adicionar` ou pressione `Enter` apos selecionar o produto.
9. Informe desconto total da venda, se existir.
10. Adicione uma ou mais especies de pagamento.
11. Para dinheiro, informe o valor recebido; o PDV calcula o troco e aplica na
    venda apenas o valor necessario para fechar o total liquido.
12. Para cartao TEF nesta fase, use especies de cartao/TEF. O modo padrao de
    homologacao e `Tef:Mode=Simulated`, com metadados nao sensiveis.
13. Clique `Finalizar venda`.
14. Quando necessario, informe valor de fechamento e clique `Fechar caixa`.

Ao finalizar, a venda e gravada no PostgreSQL local e fica pronta para o
SyncAgent enviar ao ERP:

- `pdv.sales` com `sync_status=pending_sync`;
- `pdv.sale_items`;
- `pdv.payments`.

Fluxo de sincronizacao:

- `pending_sync`: venda criada pelo PDV App;
- `sent`: SyncAgent publicou a venda no outbox como `source_system=pdv_local`;
- `accepted`: ERP aceitou a venda;
- `rejected`: ERP rejeitou a venda e o evento foi para dead letter.

O painel de venda fica bloqueado enquanto nao houver operador carregado e caixa
aberto. A tela atual nao cadastra produto durante a venda; os produtos devem
estar no catalogo local.

## Frente de caixa

Funcionalidades ja implementadas:

- bloqueio da venda sem caixa aberto;
- busca de produto por `product_id`, `external_key`, `sku`, `barcode` ou nome;
- selecao por duplo clique quando a busca retorna mais de um produto;
- carrinho com multiplos itens;
- desconto por item;
- desconto total da venda;
- remocao de item;
- autorizacao de supervisor/admin para desconto, remocao de item, remocao de
  pagamento e limpeza de venda em andamento;
- auditoria local em `pdv.operation_audits` para operacoes sensiveis;
- suprimento e sangria com autorizacao de supervisor/admin;
- resumo local do caixa com abertura, vendas por especie, dinheiro em vendas,
  suprimentos, sangrias e dinheiro esperado para fechamento;
- multiplas especies de pagamento na mesma venda;
- calculo de troco para dinheiro;
- camada TEF local configuravel com modos `Simulated`, `Disabled` e `Provider`;
- metadados TEF nao sensiveis gravados em `pdv.payments.payload.tef_metadata`;
- finalizacao usando o mesmo fluxo `pdv.sales -> SyncAgent -> ERP` ja homologado.

Para leitura completa do status:

- o servico `PDV Local Sync Agent` deve estar rodando e expondo a API local em
  `http://127.0.0.1:47891`;
- o PostgreSQL local deve estar acessivel pela connection string configurada.

Override local da connection string:

```powershell
$env:PDV_LOCAL_CONNECTION_STRING="Host=localhost;Port=5432;Database=pdv_sync;Username=pdv_sync;Password=pdv_sync"
dotnet run --project D:\GitHub\pdv-local\src\pdv-app\PdvLocal.App.csproj
```

## Testes

Testes automatizados da camada de dados:

```powershell
dotnet test D:\GitHub\pdv-local\tests\pdv-core-tests\PdvLocal.Core.Tests.csproj
```

Smoke opcional contra PostgreSQL real:

```powershell
$env:PDV_LOCAL_TEST_CONNECTION_STRING="Host=localhost;Port=5432;Database=pdv_sync;Username=pdv_sync;Password=pdv_sync"
dotnet test D:\GitHub\pdv-local\tests\pdv-core-tests\PdvLocal.Core.Tests.csproj --filter PdvDatabaseIntegrationTests
Remove-Item Env:\PDV_LOCAL_TEST_CONNECTION_STRING
```

Se `PDV_LOCAL_TEST_CONNECTION_STRING` nao estiver definida, o smoke de
integracao nao acessa banco externo.

## Homologacao visual

Homologacao visual aprovada na VM `192.168.0.184` em 2026-06-06:

- execucao na sessao grafica interativa do usuario `Suporte`;
- automacao por `UIAutomationClient`, usando `AutomationId` dos controles WPF;
- venda `PDV-20260606000010`;
- produtos ERP `1187` e `1234`;
- pagamento `dinheiro`;
- `pdv.sales.sync_status=accepted`;
- evento local aceito com `items[].product_erp_id=1187/1234`;
- ERP gravou a venda como `origem=pdv_local`, `status=faturada`, total
  `601.32`.
- venda com desconto autorizada por supervisor `PDV-20260606112301` validou:
  - bloqueio de desconto sem supervisor;
  - desconto de item `1.00`;
  - desconto total `2.00`;
  - dois registros em `pdv.operation_audits`;
  - aceite no ERP com total `598.32`.
- movimento de caixa autorizado por supervisor com motivo
  `Homologacao caixa 20260606115102` validou:
  - bloqueio de movimento sem autorizacao vigente;
  - suprimento `10.00`;
  - sangria `3.00`;
  - dois registros em `pdv.cash_movements`;
  - duas auditorias em `pdv.operation_audits`;
  - resumo com dinheiro esperado `2668.18`.
- venda tecnica TEF `PDV-HML-TEF-20260606121502` validou:
  - `pdv.payments.payload.tef_metadata.authorization_code=HML902711`;
  - outbox aceito com `tef_metadata.provider=homologation`;
  - ERP aplicou o evento e criou a venda `6467`.

Evidencia completa:

```text
D:\GitHub\pdv-local\docs\pdv-app-vm-homologation-2026-06-05.md
```

## Publicacao

```powershell
dotnet publish D:\GitHub\pdv-local\src\pdv-app\PdvLocal.App.csproj `
  -c Release `
  -r win-x64 `
  --self-contained false `
  -o D:\GitHub\pdv-local\artifacts\pdv-app\front-cashier
```

Artefato atual:

```text
D:\GitHub\pdv-local\artifacts\pdv-app\tef-provider-boundary\PdvLocal.App.exe
```

## Instalacao Windows

O pacote do instalador inclui o payload `payload\PDVApp`.

Durante a instalacao, o script copia o app para:

```text
C:\Program Files\PDVLocal\PDVApp\PdvLocal.App.exe
```

Atalhos criados:

```text
Desktop publico: PDV Local.lnk
Menu Iniciar: PDV Local\PDV Local.lnk
```

## Limites atuais

- Ainda nao cria o schema completo automaticamente pela aplicacao; criacao base
  fica nos scripts de infraestrutura/instalador. As tabelas incrementais
  `pdv.operation_audits` e `pdv.cash_movements` sao garantidas pelo PDV App ao
  iniciar.
- Nao cria operador local manualmente; operadores devem ser importados do ERP.
- Importacao de operadores depende do contrato `sync` `1.5.0`:
  `GET /v1/sync/pdv/operators:snapshot`.
- Produtos dependem do catalogo local sincronizado pelo contrato `sync` `1.7.0`:
  `GET /v1/sync/pdv/products:snapshot`.
- Especies e condicoes de pagamento sao sincronizadas pelo contrato `sync`
  `1.8.0`: `GET /v1/sync/pdv/payment-methods:snapshot`.
- A tela carrega especies de `pdv.payment_species` e condicoes de
  `pdv.payment_conditions`; quando o snapshot ainda nao existe, usa fallback
  local temporario.
- Metadados de especie/condicao sao gravados no `payload` de `pdv.payments`,
  e enviados ao ERP pelo contrato `sync` `1.9.0` no payload oficial de venda.
- Metadados TEF nao sensiveis tambem sao enviados pelo contrato `sync` `1.9.0`;
  PAN, CVV, trilha, senha, nome do portador e validade do cartao nunca devem ser
  persistidos ou enviados.
- Configuracao TEF local:
  - `Tef:Mode=Simulated`: autoriza localmente para homologacao;
  - `Tef:Mode=Disabled`: bloqueia pagamentos TEF;
  - `Tef:Mode=Provider`: falha fechado ate existir adaptador real do fornecedor;
  - variaveis opcionais: `PDV_LOCAL_TEF_MODE` e `PDV_LOCAL_TEF_PROVIDER`.
- Homologacao tecnica automatizada existe em `src/pdv-homologation` e cria venda
  multi-item com pagamento misto pelo mesmo core usado pela UI.
- Homologacao visual da UI WPF existe como evidencia na VM, mas ainda nao foi
  promovida para um teste automatizado versionado no repositorio.
- TEF real ainda nao foi implementado; a camada de provider ja existe, mas falta
  escolher fornecedor, instalar SDK/binarios e implementar o adaptador certificado.
- Cancelamento de venda ja finalizada ainda nao possui tela propria; a auditoria
  atual cobre desconto, remocao de item, remocao de pagamento, limpeza de venda
  em andamento, suprimento e sangria.
- Comprovante fiscal, impressao e TEF real ainda ficam para fases futuras.
