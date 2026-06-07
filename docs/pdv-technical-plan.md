# Plano tecnico do PDV Local

## Objetivo

Definir a arquitetura tecnica do PDV Local antes do inicio da implementacao do
aplicativo operacional de caixa. Este documento cobre o PDV App, nao substitui a
documentacao do SyncAgent ja existente.

O PDV Local deve operar em Windows, funcionar em modo offline-first e sincronizar
com o ERP por contratos versionados em `../sync`.

## Escopo do PDV App

O PDV App sera a aplicacao usada pelo operador no caixa para:

- autenticar operador local;
- abrir e fechar caixa;
- consultar produtos;
- registrar vendas;
- registrar pagamentos;
- emitir comprovantes quando a integracao fiscal/perifericos estiver definida;
- operar mesmo sem conexao com o ERP;
- exibir pendencias de sincronizacao;
- exibir estado de ativacao e conectividade.

O PDV App nao e responsavel por coletar dados diretamente do banco Arpa. Essa
funcao permanece no SyncAgent e deve continuar obedecendo a politica de leitura
somente.

## Decisoes tecnicas atuais

| Tema | Decisao |
| --- | --- |
| Plataforma | Windows desktop |
| Runtime | .NET 8 |
| UI definida para inicio | WPF |
| Banco local | PostgreSQL 17 local |
| Extensoes locais | pgvector 0.8.0, alinhado ao ERP |
| Instalacao | Instalador Windows interativo junto com SyncAgent |
| Sync | SyncAgent como Windows Service |
| Status operacional | Tray e dashboard local |
| Contratos | Sempre definidos em `../sync` |
| Identidade do cliente | Vem do CP/ERP por ativacao |
| Plano/modulos | Somente leitura no PDV, controlado pelo CP |

## Limites de seguranca

1. O PDV nunca deve gravar no banco Arpa.
2. O Arpa, quando usado, e fonte local de leitura via usuario read-only.
3. Credenciais sensiveis devem ficar protegidas por DPAPI ou mecanismo equivalente
   no Windows.
4. O cliente/operador nao deve digitar livremente o ID do cliente no CP.
5. Plano, modulos e limites devem ser recebidos do CP/ERP e exibidos como
   informacao somente leitura.
6. Toda comunicacao externa deve passar por API contratada e versionada.

## Componentes locais

| Componente | Responsabilidade |
| --- | --- |
| PDV App | Interface operacional de caixa |
| SyncAgent | Coleta local, outbox, envio ao ERP, heartbeat e reconciliacao |
| SyncAgent Tray | Icone de bandeja para suporte e status rapido |
| Dashboard local | Diagnostico tecnico via `http://127.0.0.1:47891/` |
| PostgreSQL local | Banco local do PDV e do SyncAgent |
| Instalador | Provisionar runtime, PostgreSQL, pgvector, SyncAgent, Tray e PDV App |

## Estrutura proposta no repositorio

```text
pdv-local/
  src/
    pdv-app/
    sync-agent/
    sync-agent-tray/
    sync-agent-installer/
  docs/
    pdv-technical-plan.md
```

## Modelo local inicial

O schema do PDV deve ser separado do schema tecnico do SyncAgent.

Schema tecnico existente:

```text
sync_agent
```

Schema proposto para o PDV:

```text
pdv
```

Tabelas iniciais previstas:

| Tabela | Finalidade |
| --- | --- |
| `pdv.operators` | Operadores locais do caixa |
| `pdv.cash_sessions` | Abertura e fechamento de caixa |
| `pdv.products` | Catalogo local de produtos disponiveis para venda |
| `pdv.customers` | Clientes disponiveis localmente |
| `pdv.sales` | Cabecalho da venda |
| `pdv.sale_items` | Itens da venda |
| `pdv.payments` | Pagamentos da venda |
| `pdv.settings` | Configuracoes locais nao sensiveis |

Estados recomendados para venda:

```text
draft
completed
cancelled
pending_sync
sent
accepted
rejected
```

## Fluxo operacional minimo

1. Operador abre o PDV App.
2. PDV valida se a instalacao esta ativada.
3. Operador autentica localmente.
4. Operador abre caixa.
5. Operador inicia venda.
6. PDV consulta produto no banco local.
7. Operador adiciona itens.
8. Operador informa forma de pagamento.
9. PDV finaliza venda localmente.
10. PDV registra evento para sincronizacao.
11. SyncAgent envia evento ao ERP.
12. PDV atualiza status conforme aceite/rejeicao.

## Offline-first

O PDV deve continuar vendendo quando o ERP estiver indisponivel, desde que:

- a instalacao ja esteja ativada;
- o plano local ainda esteja valido conforme politica definida;
- o operador local tenha permissao;
- o produto esteja disponivel no banco local;
- nao exista bloqueio operacional configurado.

Quando offline:

- vendas devem ser gravadas localmente;
- eventos devem ficar pendentes;
- a tela deve exibir pendencias de sincronizacao;
- o SyncAgent deve reenviar automaticamente quando a conexao voltar.

## Integracao com SyncAgent

O PDV App deve se integrar ao SyncAgent de forma local, sem depender de detalhes
internos do ERP.

Integracoes locais previstas:

| Integracao | Uso |
| --- | --- |
| API local do SyncAgent | Status, ativacao, sync manual e logs |
| Banco local | Leitura/gravacao operacional do PDV |
| Outbox local | Entrega assincrona de eventos ao ERP |

Estado atual da API local do SyncAgent:

- ja existe `GET /status`;
- ja existe `GET /setup`;
- ja existe `POST /setup/activate`;
- ja existe `GET /logs`;
- ja existe `GET /help`;
- ja existe `POST /sync-now`;
- nao existe endpoint local para o PDV publicar eventos diretamente.

A decisao atual de publicacao de vendas PDV e:

- o PDV App grava a venda operacional no schema `pdv`;
- a venda finalizada entra em `pdv.sales.sync_status=pending_sync`;
- o SyncAgent le `pdv.sales`, monta o evento `pdv_local/venda` e grava no
  outbox tecnico;
- o PDV App nao grava diretamente em `sync_agent.outbox_events`.

Alternativas consideradas:

- opcao A: PDV grava eventos diretamente em `sync_agent.outbox_events`;
- opcao B: PDV chama uma API local do SyncAgent para publicar eventos;
- opcao C: criar uma biblioteca compartilhada local de publicacao.

Para a fase atual, a abordagem escolhida preserva a fronteira operacional: o PDV
conhece venda e caixa; o SyncAgent conhece outbox, contrato e entrega ao ERP.

## Contratos necessarios em `../sync`

O contrato atual de eventos v1 ja possui envelope generico para:

- `POST /v1/sync/events:batch`;
- `entity_type` incluindo `cliente`, `produto`, `estoque`, `venda` e
  `financeiro`;
- exemplo oficial de `venda` e `financeiro` com origem Arpa.

Estado contratual atual:

- contrato `1.6.0` permite `source_system=pdv_local`;
- `pdv_local` e aceito pelo ERP apenas para `entity_type=venda`;
- schema oficial `PdvSalePayloadV1` cobre venda finalizada/cancelada, itens,
  descontos numericos e multiplos pagamentos;
- exemplo oficial de venda PDV existe em
  `../sync/examples/pdv-sale-events-batch-example.json`;
- aceite/rejeicao segue a resposta do endpoint batch ja existente.

Extensoes futuras que ainda exigem contrato em `../sync`:

- fechamento de caixa;
- sangria e suprimento;
- cancelamento operacional detalhado, quando precisar de evento proprio alem da
  venda com `status=cancelled`;
- campos adicionais especificos do provedor TEF, caso o contrato `1.9.0` nao
  cubra a homologacao do provedor escolhido;
- snapshot de produtos completo para operacao de supermercado;
- snapshot de clientes, quando o PDV passar a identificar cliente na venda;
- consulta/snapshot de plano, modulos e limites operacionais.

Toda mudanca de contrato deve atualizar exemplos oficiais e changelog no
repositorio `../sync`.

## Instalador

O instalador final deve instalar e validar:

- Microsoft Visual C++ Runtime;
- .NET Desktop Runtime 8;
- PostgreSQL 17 local;
- pgvector 0.8.0;
- banco `pdv_sync`;
- schemas `sync_agent` e `pdv`;
- SyncAgent como Windows Service;
- SyncAgent Tray;
- PDV App;
- atalhos do PDV App;
- tela de ativacao/configuracao inicial.

## MVP tecnico

O primeiro MVP do PDV deve entregar:

1. projeto `src/pdv-app`;
2. tela principal WPF;
3. leitura do status de ativacao do SyncAgent;
4. schema `pdv` criado no PostgreSQL local;
5. cadastro local minimo de operador;
6. abertura e fechamento de caixa;
7. venda simples com produto, quantidade e pagamento;
8. persistencia local da venda;
9. publicacao de evento pendente para sincronizacao;
10. tela de pendencias de sincronizacao.

## Estado atual da Fase 7

Concluido ate 2026-06-06:

- frente de caixa WPF com bloqueio de venda sem operador e caixa aberto;
- operadores importados do ERP para o banco local;
- catalogo de produtos importado do ERP para `pdv.products`;
- especies e condicoes de pagamento importadas do ERP;
- carrinho com multiplos itens;
- desconto por item e desconto total;
- autorizacao local por operador `admin`/`supervisor` para desconto, remocao de
  item, remocao de pagamento e limpeza de venda em andamento;
- auditoria local em `pdv.operation_audits` para operacoes sensiveis do frente
  de caixa;
- suprimento e sangria locais com autorizacao de operador `admin`/`supervisor`;
- resumo local de fechamento com vendas por especie, dinheiro em vendas,
  suprimentos, sangrias e dinheiro esperado;
- pagamentos locais com metadados de especie e condicao;
- contrato `sync` `1.9.0` promovendo especie, condicao, parcelas, flags
  operacionais e metadados TEF nao sensiveis no payload oficial de venda;
- camada local TEF configuravel com modos `Simulated`, `Disabled` e `Provider`;
- validacao local bloqueando chaves sensiveis em `tef_metadata`;
- persistencia de `pdv.payments.payload.tef_metadata` e envio pelo SyncAgent ao
  ERP;
- pagamento misto validado tecnicamente pelo projeto
  `src/pdv-homologation`;
- venda PDV enviada pelo SyncAgent para o ERP;
- contrato `sync` `1.8.1` com `items[].product_erp_id`;
- ERP preferindo `product_erp_id` para evitar colisao com `codigo_arpa`;
- ERP resolvendo especie e condicao da venda PDV por IDs externos do snapshot
  de pagamentos;
- homologacao tecnica TEF simulada aprovada com a venda
  `PDV-HML-TEF-20260606102217`;
- homologacao tecnica da camada TEF com metadados estruturados aprovada com a
  venda `PDV-HML-TEF-20260606121502`;
- homologacao visual de desconto com supervisor aprovada com a venda
  `PDV-20260606112301`;
- homologacao visual de suprimento/sangria aprovada com o motivo
  `Homologacao caixa 20260606115102`;
- homologacao visual da UI WPF aprovada na VM `192.168.0.184` com a venda
  `PDV-20260606000010`.

Evidencia operacional:

```text
D:\GitHub\pdv-local\docs\pdv-app-vm-homologation-2026-06-05.md
```

Pendencias tecnicas restantes da Fase 7:

1. escolher fornecedor/provedor TEF e implementar o adaptador real certificado.

Pendencia posterior fora do nucleo desta fase:

- definir etapa fiscal/comprovante, incluindo impressao e NFC-e quando o escopo
  fiscal for confirmado.
- implementar fluxo proprio para cancelamento de venda ja finalizada, incluindo
  estorno fiscal/TEF quando essas integracoes existirem.

## Gates antes de implementacao

Antes de iniciar codigo de uma fase, validar:

1. se a mudanca exige contrato em `../sync`;
2. se existe impacto no instalador Windows;
3. se existe impacto no banco local `pdv_sync`;
4. se existe risco de gravacao indevida no banco Arpa;
5. se a VM de homologacao deve ser atualizada;
6. quais evidencias de teste serao exigidas.

## Fases de desenvolvimento

### Fase 1 - Fundacao

- Criar `src/pdv-app`.
- Definir projeto WPF .NET 8.
- Criar acesso ao PostgreSQL local.
- Ler status da API local do SyncAgent.
- Criar primeira tela de status/ativacao.

Status atual da Fase 1:

- `src/pdv-app` criado como WPF em .NET 8;
- projeto adicionado a `pdv-local.sln`;
- primeira tela criada com leitura de `GET http://127.0.0.1:47891/status`;
- leitura do PostgreSQL local adicionada via `ConnectionStrings:PdvLocalDb`;
- configuracao inicial centralizada em `src/pdv-app/appsettings.json`;
- overrides por ambiente `PDV_LOCAL_CONNECTION_STRING` e
  `PDV_LOCAL_SYNC_AGENT_URL`;
- instalador atualizado para copiar o PDV App para
  `C:\Program Files\PDVLocal\PDVApp`;
- instalador atualizado para criar atalho `PDV Local.lnk` na area de trabalho e
  no Menu Iniciar;
- PDV App copiado para a VM de homologacao em
  `C:\Program Files\PDVLocal\PDVApp`;
- atalhos do PDV App criados e validados na VM;
- smoke de processo do `PdvLocal.App.exe` aprovado via WinRM;
- script `infra/postgres/init/03-create-pdv-schema.sql` criado;
- bootstrap Windows atualizado para aplicar o schema `pdv`;
- schema `pdv` aplicado e validado na VM de homologacao;
- botao de atualizacao criado;
- botao de ativacao abre `http://127.0.0.1:47891/setup`;
- publish framework-dependent gerado em `artifacts/pdv-app/foundation`;
- build da solution validado com sucesso.

Pendencias restantes da Fase 1:

- nenhuma pendencia aberta. A abertura visual em sessao interativa da VM foi
  validada na homologacao de 2026-06-05.

### Fase 2 - Banco local do PDV

- Criar migrations/scripts do schema `pdv`.
- Criar tabelas minimas.
- Criar seed local seguro para operador inicial ou fluxo de primeiro acesso.

Status atual da Fase 2:

- schema `pdv` criado por script versionado em `infra/postgres/init`;
- tabelas iniciais criadas: `operators`, `cash_sessions`, `products`,
  `customers`, `sales`, `sale_items`, `payments`, `settings`;
- PDV App exibe contadores de operadores, produtos, vendas e caixas abertos;
- decisao de seguranca: operadores do PDV Local serao importados do ERP; o PDV
  nao cria usuarios locais manualmente;
- contrato `sync` `1.5.0` definido para snapshot de operadores:
  `GET /v1/sync/pdv/operators:snapshot`;
- endpoint `GET /v1/sync/pdv/operators:snapshot` implementado no ERP em
  `sync_api`, autenticado por bearer token do SyncAgent;
- snapshot de operadores do ERP nao retorna senha, hash de senha, flag
  `is_superuser` nem qualquer segredo de autenticacao;
- `updated_at_utc` do operador e derivado de `updated_at`/`modified_at` quando o
  modelo possuir esses campos; no `User` padrao do Django, usa `last_login` ou
  `date_joined` como fallback ate existir trilha propria de alteracao de
  operadores;
- testes focados do ERP `manage.py test sync_api --keepdb` passaram com 42
  testes em 2026-06-05;
- SyncAgent implementa importacao local de operadores via
  `ErpPdvSnapshot.Enabled=true`, consumindo
  `GET /v1/sync/pdv/operators:snapshot`;
- operadores importados sao gravados em `pdv.operators` com UUID local
  deterministico derivado do ID do ERP, preservando `external_operator_id`,
  `login`, `display_name`, `role`, `active` e `permissions`;
- `pdv.operators.password_hash` fica vazio para operadores importados; o ERP nao
  envia segredo e o PDV nao cria senha local nesta fase;
- schema `pdv.operators` recebeu colunas aditivas `external_operator_id` e
  `permissions`, mantendo compatibilidade com bases ja instaladas;
- aba Logs do dashboard local passa a exibir a tarefa
  `pdv_operator_snapshot` com status da ultima importacao de operadores;
- se o ERP retornar `has_more=true`, o SyncAgent importa o lote recebido, mas
  nao avanca o watermark para evitar perda silenciosa de operadores;
- build da solution `dotnet build D:\GitHub\pdv-local\pdv-local.sln` validado
  com sucesso em 2026-06-05;
- VM de homologacao atualizada em 2026-06-05; apos `sync-now`, a importacao de
  operadores retornou HTTP 200 e `pdv.operators` passou para `28` registros;
- criada biblioteca `src/pdv-core` para concentrar acesso ao banco local fora da
  UI WPF;
- PDV App passou a usar `PdvLocal.Core.PdvDatabaseStatusReader`;
- camada inicial de escrita criada para:
  - leitura de operador ativo por login;
  - abertura de caixa;
  - fechamento de caixa;
  - gravacao transacional de venda concluida com itens e pagamentos;
- regras de validacao criadas para papel de operador, abertura/fechamento de
  caixa e venda concluida;
- projeto `tests/pdv-core-tests` criado com testes automatizados de validacao;
- smoke de integracao com banco local pode ser executado quando
  `PDV_LOCAL_TEST_CONNECTION_STRING` estiver definido;
- tentativa de smoke direto contra `192.168.0.184:5432` falhou por conectividade
  TCP externa; a VM segue validada via WinRM/psql;
- `dotnet test D:\GitHub\pdv-local\pdv-local.sln` validado com 9 testes
  aprovados;
- `dotnet build D:\GitHub\pdv-local\pdv-local.sln` validado com 0 avisos e 0
  erros;
- pacote `artifacts\sync-agent-installer` regenerado e validado; payload do PDV
  App contem `PdvLocal.Core.dll`;
- VM de homologacao validada com:
  - `operators=28`;
  - `products=3`;
  - `sales=5`;
  - `accepted_sales=5`;
  - `open_cash_sessions=1`;
  - `pending_outbox_events=0`;
  - `dead_letter_events=0`.

Pendencias restantes da Fase 2:

- executar smoke de integracao do `PdvLocal.Core` em ambiente com PostgreSQL
  acessivel diretamente pelo runner, ou expor temporariamente o PostgreSQL da VM
  para essa finalidade.

### Fase 3 - Caixa e venda simples

- Abrir caixa.
- Lancar venda.
- Adicionar produto.
- Registrar pagamento.
- Finalizar venda local.

Status atual da Fase 3:

- PDV App recebeu painel operacional inicial de caixa;
- operador importado pode ser carregado por login;
- caixa pode ser aberto para operador ativo;
- caixa aberto existente e reutilizado quando o operador ja possui um caixa em
  aberto;
- caixa pode ser fechado informando valor de fechamento;
- venda simples de um item pode ser gravada localmente;
- quando nao houver catalogo importado, o fluxo permite criar/atualizar produto
  manual local com `source_system=pdv_manual`;
- venda concluida e gravada em `pdv.sales` com `sync_status=pending_sync`;
- itens sao gravados em `pdv.sale_items`;
- pagamentos sao gravados em `pdv.payments` com status `captured`;
- SyncAgent possui publisher de vendas PDV:
  - le `pdv.sales` em `pending_sync`;
  - cria evento outbox `source_system=pdv_local`, `entity_type=venda`,
    `schema_version=v1.0`;
  - usa `sale_id` como `event_id` para idempotencia;
  - muda a venda para `sent` ao publicar no outbox;
  - muda para `accepted` quando o ERP aceita o evento;
  - muda para `rejected` quando o ERP rejeita o evento;
- repositório de venda valida que o caixa pertence ao operador e esta `open`
  antes de gravar;
- testes automatizados atualizados para 11 testes aprovados;
- build da solution validado com 0 avisos e 0 erros;
- pacote do instalador regenerado e validado;
- VM de homologacao atualizada com o PDV App novo; smoke de processo aprovado.

Pendencias restantes da Fase 3:

- nenhuma pendencia tecnica da venda simples homologada.
- a UX atual nao esta pronta para piloto de supermercado; a proxima fase deve
  substituir a tela tecnica por uma frente de caixa operacional.

### Fase 4 - Sincronizacao de vendas

- Contrato formalizado em `../sync` como versao `1.6.0`.
- ERP aceita `source_system=pdv_local` apenas para `entity_type=venda`.
- SyncAgent publica vendas locais como eventos `pdv_local/venda`.
- Status local da venda:
  - `pending_sync`: criada no PDV e ainda nao publicada no outbox;
  - `sent`: publicada no outbox e aguardando aceite/rejeicao;
  - `accepted`: ERP aceitou o evento;
  - `rejected`: ERP rejeitou o evento e o outbox foi para dead letter.
- Dashboard local:
  - `GET /status` retorna `pdv_sales_summary`;
  - `GET /` exibe cards de vendas PDV pendentes, enviadas, aceitas e
    rejeitadas;
  - `GET /help` explica o significado de `pdv_sales_summary`.
- Reprocessamento operacional:
  - vendas `rejected` aparecem no dashboard local;
  - o botao `Reprocessar` chama `POST /pdv-sales/reprocess`;
  - a rotina remove o dead-letter, coloca o outbox em `pending`, muda a venda
    para `sent` e dispara um ciclo manual;
  - quando o ERP aceita o reenvio, a venda volta para `accepted`.
- Matriz de integracao executada:
  - payload invalido de venda sem itens: ERP rejeitou, venda local virou
    `rejected`, outbox foi para `dead_letter` com motivo claro;
  - ERP offline: venda valida ficou `sent`, outbox ficou `pending` com
    `HttpRequestException`, respeitou backoff e depois foi aceita quando o ERP
    voltou;
  - estado final da VM ficou sem pendentes e sem dead-letter residual.
- Venda real criada pelo PDV App via UI foi validada em 2026-06-05:
  - `sale_id=44dde029-510e-46ed-b047-03ae740e99c3`;
  - `sale_number=PDV-20260605150230`;
  - status local `accepted`;
  - ERP criou `vendas_venda` com `origem=pdv_local`, status `faturada`,
    total `10.00` e 1 item.

### Fase 5 - Instalacao integrada

- Incluir PDV App no instalador.
- Criar atalhos.
- Validar instalacao limpa em VM.
- Validar atualizacao sem perda de dados.

### Fase 6 - Piloto operacional

- Rodar fluxo completo em VM.
- Vender offline.
- Reenviar online.
- Confirmar chegada no ERP.
- Documentar evidencias.

### Fase 7 - Frente de caixa de supermercado

Objetivo: trocar a tela tecnica de homologacao por uma tela de operacao rapida,
adequada para caixa de supermercado.

Decisoes obrigatorias desta fase:

- venda so pode iniciar com instalacao ativada, operador carregado e caixa
  aberto;
- se nao houver caixa aberto, a tela de venda deve ficar bloqueada;
- produtos devem vir do catalogo local sincronizado, nao de cadastro manual no
  momento da venda;
- o campo principal de venda deve aceitar ID interno, SKU/codigo externo, codigo
  de barras e busca por nome;
- itens devem ser lancados um a um, com foco permanente no campo de entrada;
- a venda deve manter carrinho local antes da finalizacao;
- descontos devem existir por item e no total da venda;
- desconto deve registrar valor, tipo, operador, motivo e, quando aplicavel,
  operador supervisor que autorizou;
- fechamento deve aceitar condicao de pagamento e multiplas especies na mesma
  venda;
- pagamentos com cartao devem passar por integracao TEF quando a especie exigir;
- pagamento em dinheiro deve calcular troco;
- finalizar venda deve ser bloqueado quando a soma dos pagamentos for menor que
  o total liquido;
- cancelamento de item e cancelamento de venda devem exigir motivo;
- operacoes sensiveis devem respeitar permissoes vindas do ERP.

Mudancas locais entregues ou previstas:

- modelo de venda em andamento separado da venda finalizada ja existe na UI;
- busca de produto por `product_id`, `external_key`, `sku`, `barcode`
  e nome;
- tabelas locais para especies e condicoes de pagamento sincronizadas do
  ERP;
- `pdv.payments` enriquecida com IDs externos da especie/condicao de pagamento;
- `pdv.payments` preparada para dados TEF nao sensiveis: provedor, ID da
  transacao, NSU, codigo de autorizacao, bandeira, adquirente, parcelas, status
  TEF e referencia de comprovante;
- auditoria local entregue para desconto, remocao de item, remocao de pagamento
  e limpeza de venda em andamento;
- auditoria local entregue para suprimento e sangria;
- adicionar auditoria local para cancelamento de venda finalizada e fechamento de
  caixa, caso a politica operacional exija autorizacao nessas situacoes;
- resumo de caixa por especie entregue para fechamento local;
- revisar indices de produto para operacao com leitura de codigo de barras.

#### Pagamento via TEF

O TEF deve ser tratado como uma integracao separada da logica de venda. A venda
controla carrinho, totais e pagamentos; o adaptador TEF controla comunicacao com
pinpad/provedor, autorizacao, cancelamento e comprovantes.

Regras de seguranca:

- o PDV nao deve armazenar numero completo do cartao, tarja, CVV, senha ou dados
  sensiveis de portador;
- o PDV deve persistir somente identificadores operacionais nao sensiveis
  exigidos para conciliacao, suporte e envio ao ERP;
- comprovantes TEF devem ser tratados como documento operacional, sem expor dado
  sensivel alem do permitido pelo provedor;
- credenciais do provedor TEF devem usar DPAPI ou mecanismo equivalente;
- a homologacao TEF real depende de fornecedor/provedor definido e ambiente de
  teste certificado.

Estado atual em 2026-06-06:

- o PDV possui fronteira local `ITefPaymentProvider`;
- `Simulated` autoriza TEF para homologacao e gera `tef_metadata` nao sensivel;
- `Disabled` bloqueia pagamentos TEF;
- `Provider` falha fechado ate existir adaptador real do fornecedor;
- venda `PDV-HML-TEF-20260606121502` validou persistencia local, outbox,
  contrato `1.9.0` e aplicacao no ERP;
- ainda falta o SDK/protocolo do fornecedor real, binarios, credenciais,
  instalador do provider, ambiente de homologacao e fluxo de cancelamento/estorno
  TEF.

Estados minimos da transacao TEF:

```text
requested
in_progress
authorized
declined
cancelled
reversed
failed
```

Fluxo esperado:

1. Operador escolhe especie de pagamento do tipo cartao/TEF.
2. PDV envia valor, tipo de operacao, parcelas e identificador da venda ao
   adaptador TEF.
3. Adaptador TEF conduz a transacao no pinpad/provedor.
4. Se autorizado, PDV grava pagamento como capturado com dados nao sensiveis.
5. Se negado/falhou, PDV nao fecha a venda e permite nova tentativa ou outra
   especie.
6. Se a venda for cancelada apos autorizacao, deve existir fluxo de cancelamento
   ou estorno conforme regra do provedor.

Abstracao tecnica prevista:

- `ITefProvider` ou equivalente no `PdvLocal.Core`;
- implementacao especifica por provedor TEF somente depois da escolha do
  fornecedor;
- simulador TEF local para testes automatizados e homologacao sem pinpad;
- logs tecnicos sem dados sensiveis;
- idempotencia por `sale_id` + `payment_id` + identificador TEF.

Dados TEF candidatos para contrato/sync:

- `tef_provider`;
- `tef_transaction_id`;
- `nsu`;
- `authorization_code`;
- `card_brand`;
- `acquirer`;
- `installments`;
- `tef_status`;
- `receipt_reference`.

Gates da Fase 7:

- contrato `../sync` definiu especie, condicao, parcelas, flags operacionais e
  metadados TEF nao sensiveis como campos opcionais aditivos em `payments[]` no
  contrato `1.9.0`;
- snapshot ERP -> PDV para produtos, especies e condicoes de pagamento ja esta
  implementado;
- politica inicial de permissao ja esta implementada para desconto, remocao de
  item, remocao de pagamento e limpeza de venda em andamento, com autorizacao de
  operador `admin`/`supervisor` e auditoria local em `pdv.operation_audits`;
- definir fornecedor/provedor TEF e requisitos de instalacao local;
- definir regras de cancelamento de venda ja finalizada junto com o fluxo fiscal,
  estorno e TEF;
- definir politica para venda com TEF quando o provedor estiver indisponivel;
- definir comportamento quando produto nao for encontrado;
- definir se venda em rascunho deve sobreviver ao fechamento inesperado do app.

Criterios de pronto da Fase 7:

- operador abre caixa antes de vender;
- tela de venda inicia bloqueada sem caixa aberto;
- item pode ser lancado por codigo de barras, ID, SKU/codigo externo ou busca por
  nome;
- carrinho aceita multiplos itens;
- desconto por item e desconto total funcionam e ficam auditados;
- venda aceita multiplas especies de pagamento;
- pagamento TEF possui adaptador/simulador e nao armazena dados sensiveis de
  cartao;
- troco em dinheiro e calculado;
- venda finalizada sincroniza e chega ao ERP como `pdv_local`;
- fluxo completo passa em VM com evidencia documentada.

Status da primeira entrega da Fase 7 em 2026-06-05:

- PDV App deixou de usar o painel tecnico de venda simples e passou a exibir uma
  frente de caixa inicial;
- venda fica bloqueada ate existir operador carregado e caixa aberto;
- busca de produto implementada no `PdvLocal.Core` por `product_id`,
  `external_key`, `sku`, `barcode` e nome;
- tela permite selecionar produto por duplo clique quando houver varios
  resultados;
- carrinho em memoria aceita multiplos itens;
- desconto por item e desconto total da venda foram adicionados ao fluxo;
- pagamentos multiplos foram adicionados ao fluxo;
- pagamento em dinheiro calcula troco, mas grava na venda somente o valor
  aplicado ao total liquido;
- cartao possui especies TEF simuladas para preparar o fluxo sem pinpad/provedor
  real;
- venda finalizada continua usando `pdv.sales.sync_status=pending_sync`, sem
  mudanca de contrato nesta entrega;
- validacoes do core foram reforcadas para impedir desconto maior que item,
  linha duplicada e venda negativa;
- `dotnet build D:\GitHub\pdv-local\pdv-local.sln` passou com 0 erros;
- `dotnet test D:\GitHub\pdv-local\pdv-local.sln` passou com 14 testes;
- smoke local do `PdvLocal.App.exe` iniciou e permaneceu em execucao por 5
  segundos sem erro imediato.

Status do catalogo de produtos ERP -> PDV em 2026-06-05:

- contrato `sync` `1.7.0` definido para snapshot de produtos:
  `GET /v1/sync/pdv/products:snapshot`;
- endpoint implementado no ERP em `sync_api`, autenticado pelo bearer token do
  SyncAgent e restrito ao `instance_id` provisionado;
- contrato e implementacao permitem lote de ate `5000` produtos por chamada para
  atender o catalogo inicial da base de desenvolvimento;
- SyncAgent importa produtos com `ErpPdvSnapshot.Enabled=true`, gravando em
  `pdv.products` com UUID local deterministico derivado do ID do ERP;
- campos sincronizados para venda: `product_id`, `external_key`, `sku`,
  `barcode`, `name`, `unit`, `price`, `active`, `updated_at_utc`,
  `deleted_at_utc` e `payload`;
- se o ERP retornar `has_more=true`, o SyncAgent importa o lote recebido, mas
  nao avanca o watermark para evitar perda silenciosa de produtos;
- ERP Docker integrado reconstruido em 2026-06-05;
- VM de homologacao atualizada em 2026-06-05 com backup em
  `C:\ProgramData\PDVLocal\backups\SyncAgent-products-snapshot-limit5000-20260605-185526`;
- apos `sync-now`, `runtime_status=idle`, `last_error` vazio e
  `pdv.products` passou a conter `1565` produtos do ERP, sendo `1388` ativos;
- estado local `sync_agent.agent_state` para `pdv.products.snapshot` registrou
  `succeeded=true` e `response_status_code=200`.

Status de especies e condicoes de pagamento ERP -> PDV em 2026-06-05:

- contrato `sync` `1.8.0` definido para snapshot de pagamento:
  `GET /v1/sync/pdv/payment-methods:snapshot`;
- endpoint implementado no ERP em `sync_api`, autenticado pelo bearer token do
  SyncAgent e restrito ao `instance_id` provisionado;
- o snapshot retorna duas listas no mesmo ciclo: `payment_species` e
  `payment_conditions`;
- SyncAgent grava especies em `pdv.payment_species` e condicoes em
  `pdv.payment_conditions`;
- classificacao operacional inicial da especie:
  `cash`, `card`, `pix`, `voucher`, `credit` ou `other`;
- especies `card` marcam `requires_tef=true`; especie `cash` marca
  `allows_change=true`;
- como os modelos atuais do ERP nao possuem trilha de `updated_at`, o endpoint
  envia snapshot completo idempotente desses cadastros pequenos;
- `dotnet build D:\GitHub\pdv-local\pdv-local.sln` passou com 0 erros;
- `dotnet test D:\GitHub\pdv-local\pdv-local.sln` passou com 14 testes;
- ERP `manage.py test sync_api --keepdb` passou com 52 testes;
- VM de homologacao atualizada em 2026-06-05 com backup em
  `C:\ProgramData\PDVLocal\backups\SyncAgent-payment-methods-snapshot-20260605-195629`;
- apos `sync-now`, `runtime_status=idle`, `last_error` vazio,
  `pdv.payment_species=52` e `pdv.payment_conditions=78`;
- estado local `sync_agent.agent_state` para `pdv.payment_methods.snapshot`
  registrou `succeeded=true`, `imported=130` e `response_status_code=200`.

Status da UI usando catalogo sincronizado de pagamento em 2026-06-05:

- `PdvLocal.Core` recebeu `PdvPaymentCatalogRepository` para leitura de
  `pdv.payment_species` e `pdv.payment_conditions`;
- a tela de frente de caixa passou a carregar especies e condicoes ativas do
  banco local sincronizado;
- o combo de especie deixou de usar opcoes fixas no XAML;
- foi adicionado combo de condicao de pagamento no fechamento;
- a UI ainda aceita fallback local quando o snapshot nao existir, para preservar
  ambiente de desenvolvimento/instalacao incompleta;
- pagamentos gravados em `pdv.payments` continuam mantendo `payment_method` para
  compatibilidade e passam a registrar metadados locais em `payload`;
- metadados locais gravados por pagamento: `payment_species_id`,
  `payment_species_external_key`, `payment_species_kind`,
  `payment_condition_id`, `payment_condition_external_key`, `installments`,
  `requires_tef` e `allows_change`;
- dados novos ainda nao foram promovidos para contrato oficial de venda enviada
  ao ERP; isso fica para a etapa aditiva de contrato/TEF;
- `dotnet build D:\GitHub\pdv-local\pdv-local.sln` passou com 0 erros;
- `dotnet test D:\GitHub\pdv-local\pdv-local.sln` passou com 15 testes;
- VM de homologacao atualizada em 2026-06-05 com backup em
  `C:\ProgramData\PDVLocal\backups\PDVApp-payment-catalog-ui-20260605-201227`;
- PDV App foi reaberto na sessao grafica do usuario `Suporte`, `SessionId=2`,
  e permaneceu em execucao apos smoke de 10 segundos.

Status da homologacao tecnica de venda multi-item e pagamento misto em
2026-06-05:

- criado projeto `src/pdv-homologation` para executar homologacoes tecnicas pelo
  mesmo `PdvLocal.Core` usado pela UI;
- venda tecnica criada na VM com 2 itens do catalogo sincronizado e pagamento
  misto `dinheiro + Pix`;
- primeira execucao revelou bug real de integracao: o ERP interpretava
  `product_external_key` numerico como `codigo_arpa`, causando associacao de
  produto incorreta;
- contrato `sync` atualizado para `1.8.1`, adicionando campo opcional
  `items[].product_erp_id` no payload de venda PDV Local;
- ERP passou a resolver item PDV por `product_erp_id` antes de usar
  `product_external_key`;
- SyncAgent passou a enviar `product_erp_id` quando `pdv.products.source_system`
  for `erp`;
- ERP `manage.py test sync_api --keepdb` passou com 53 testes;
- `dotnet build D:\GitHub\pdv-local\pdv-local.sln` passou com 0 erros;
- `dotnet test D:\GitHub\pdv-local\pdv-local.sln` passou com 15 testes;
- ERP Docker integrado reconstruido e SyncAgent da VM atualizado com backup em
  `C:\ProgramData\PDVLocal\backups\SyncAgent-product-erp-id-fix-20260605-210210`;
- venda corrigida `PDV-HML-20260605210240` foi aceita no PDV local com
  `sync_status=accepted`, evento `accepted`, `product_erp_id=1187/1234` e
  `dead_letter=0`;
- ERP criou `vendas_venda` `id=6463`, `origem=pdv_local`, `status=faturada`,
  `total=601.32`, especie `dinheiro` e 2 itens;
- os itens do ERP ficaram associados aos produtos corretos:
  `1187:2098, 2099, 9098 6102: PAINEL TECLADO` e
  `1234:2098PP.LC: PCI PRINCIPAL`.

Pendencias da Fase 7 apos as entregas de frente de caixa:

- implementar TEF real apos escolha do fornecedor/provedor.

## Decisoes ainda pendentes

1. Definir fluxo fiscal/comprovante para fases futuras.
2. Definir regras de autorizacao para cancelamento de venda ja finalizada.
   Desconto, remocao de item, remocao de pagamento, limpeza de venda em
   andamento, suprimento e sangria ja exigem supervisor/admin e gravam auditoria
   local.
3. Definir bloqueios quando plano/licenca estiver expirado.
4. Definir comportamento de produto nao encontrado durante venda.
5. Definir persistencia e recuperacao de venda em andamento apos queda do app.
6. Definir fornecedor/provedor TEF, modelo de pinpad, instalador e ambiente de
   homologacao.
7. Definir quais campos TEF o ERP precisa receber para conciliacao e suporte.

## Criterios de pronto do MVP tecnico

Status: concluido em 2026-06-05 para o escopo tecnico inicial.

O MVP tecnico sera considerado pronto quando:

- o PDV App abrir em Windows;
- o app conseguir ler o status do SyncAgent;
- o banco local tiver schema `pdv`;
- uma venda simples puder ser gravada localmente;
- a venda gerar pendencia de sincronizacao;
- houver documentacao de execucao local e validacao em VM.

Evidencia atual:

- PDV App abriu em Windows na VM;
- SyncAgent local respondeu `status=ok`;
- schema `pdv` aplicado;
- venda simples criada pela UI;
- venda sincronizada e aceita pelo ERP;
- pendencias e dead-letter zerados;
- evidencia registrada em `docs/pdv-app-vm-homologation-2026-06-05.md`.
