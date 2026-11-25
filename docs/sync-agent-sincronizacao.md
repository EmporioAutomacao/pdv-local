# Sincronizacao do SyncAgent (Arpa -> ERP)

Visao de ponta a ponta de como o SyncAgent le o Arpa, guarda os eventos na fila local,
envia ao ERP e trata o que da errado. Vale para o SyncAgent **1.6.59**.

Este documento explica o funcionamento. Os detalhes de tela e de incidente ficam nos
documentos especificos, sem repeticao:

| Assunto | Documento |
|---|---|
| Tela Configuracoes > Arpa, campo a campo | [sync-agent-configuracoes-arpa.md](sync-agent-configuracoes-arpa.md) |
| Incidentes e passos de correcao | [sync-agent-runbook-incidentes.md](sync-agent-runbook-incidentes.md) |
| Vinculos de catalogo (especie, condicao, conta, vendedor) | [sync-agent-catalogo-vinculos-arpa.md](sync-agent-catalogo-vinculos-arpa.md) |
| Avisos enviados ao ERP | [sync-agent-avisos-no-erp.md](sync-agent-avisos-no-erp.md) |
| Politica do usuario read-only do Arpa | [arpa-readonly-security-policy.md](arpa-readonly-security-policy.md) |
| Build, deploy e VM de homologacao | [development-structure-and-vm-runbook.md](development-structure-and-vm-runbook.md) |
| Contratos de API e eventos | `../sync` (fonte de verdade) |

## 1. Visao geral

```text
 Arpa (Postgres do cliente)          SyncAgent (Windows)                         ERP
 ---------------------------         ---------------------------------           -----------------
 views sync_export.*  --leitura-->   coletor -> fila local (outbox_events)
 (usuario read-only)                              |
                                                  +--- envio em lote ------->   /v1/sync/events:batch
                                                  |    (202 + rejected_events)
                                     heartbeat <--+--- comandos, licenca,  <--   /v1/sync/heartbeat
                                                  |    re-sync sob demanda
                                     PDV local <--+--- snapshots ---------  <--  operadores, produtos,
                                                                                 pagamentos, clientes
```

O agente nunca grava no Arpa. Ele le as views `sync_export.*`, que o botao **Preparar
views** cria com um usuario DBA informado na hora, e transforma cada linha num evento.

## 2. O ciclo

O `Worker` roda um ciclo a cada `SyncAgent:PollingIntervalSeconds` (padrao 30 s, minimo 5 s)
ou na hora quando alguem clica em **Sincronizar**, **Sincronizar tudo**, **Salvar** ou
**Preparar views**. A ordem e sempre esta:

1. Se o agente nao esta ativado, espera a ativacao (`/setup`).
2. Renova o token do ERP. Se falhar (rede fora, plano suspenso), encerra so este ciclo.
3. **Coleta Arpa**, conexao por conexao (secao 3), incluindo o reenvio "Sincronizar tudo"
   em andamento (secao 7).
4. Publica as vendas feitas no PDV local.
5. **Envio ao ERP**: um lote por ciclo (secao 6).
6. Encerramento automatico de dead-letter superado, so se estiver ligado (secao 10).
7. Reconciliacao da janela das ultimas 24 h (secao 9).
8. Snapshots do ERP para o PDV: operadores, produtos, formas de pagamento e clientes.
9. **Heartbeat** (secao 8). A resposta traz re-sync sob demanda e atualizacao do aplicativo.

Uma falha num ciclo vira `degraded` em `/status` e o ciclo seguinte tenta de novo. Uma
entidade Arpa quebrada (view ausente, permissao negada) e pulada e nao derruba o ciclo.

O log do ciclo aparece ao vivo em Configuracoes > Arpa (`GET /config/arpa/sync-log`).

## 3. Coleta Arpa

### Conexoes e entidades
Cada conexao Arpa (uma por Loja/Estoque do ERP) tem toggles por entidade. Cada entidade le
uma view:

| entity_type | View | Observacao |
|---|---|---|
| `produto` | `sync_export.produtos` | Exige tabela de preco escolhida quando o ERP tem mais de uma |
| `cliente` | `sync_export.clientes` | |
| `estoque` | `sync_export.estoque` | Grava saldo so com "Controla o estoque desta Loja" |
| `venda` | `sync_export.vendas` | Ligar Vendas liga Financeiro junto |
| `financeiro` | `sync_export.financeiro` | Titulos e baixas |
| `compra` | `sync_export.compras` | Best-effort |
| `cobranca` | `sync_export.cobranca` | Best-effort |
| `plano_historico` | `sync_export.plano_historico` | Best-effort |
| `catalogo` | `sync_export.catalogos` | Especies, condicoes, contas e vendedores; coletado sempre |

Toda view segue o mesmo contrato: `entity_key, occurred_at_utc, payload_json, trace_id`.
Para estoque, venda, compra e catalogo o agente acrescenta `loja_codigo` (o nome da Loja no
ERP). Para estoque, venda e compra tambem acrescenta `empresa_cnpj` e `empresa_nome` quando a
view nao os traz.

### Quando a conexao nao e coletada (pausa)
A conexao fica **PAUSADA**, sem mexer em marcador nenhum, quando:
- as views do Arpa estao numa versao anterior a exigida pelo agente (`ararasuite:sync_export_versao`
  no schema `sync_export`). A correcao e rodar **Preparar views**;
- o banco local nao aceita um `entity_type` coletado (rotina "Corrigir
  outbox_events_entity_type_check");
- falta escolher a tabela de preco.

Quando o motivo e corrigido, a conexao retoma de onde parou.

Um tipo que o ERP ainda nao declara aceitar (`accepted_entity_types` no heartbeat, contrato
2.19.0) nao e coletado: aparece "aguardando o ERP ser atualizado". Sem esse controle, o ERP
rejeitaria o evento e ele iria para dead-letter.

### Marcador de leitura (watermark)
Cada entidade de cada conexao tem um cursor `(occurred_at_utc, entity_key)` em
`agent_state`, chave `collector.arpa.<conexao>.<entidade>.watermark`. Regras
(`ArpaCollectionCursor`, `ArpaCollectionPager`):

- A leitura e paginada por `(occurred_at_utc, entity_key COLLATE "C")`, de modo que linhas com
  o mesmo instante nao se perdem na virada do lote.
- Le no maximo 10 paginas por entidade e por ciclo; o resto continua no ciclo seguinte.
- **Nunca le linha com data no futuro e nunca grava posicao no futuro.** Um marcador
  encontrado no futuro (estado deixado por views antigas) e descartado uma vez, e a entidade
  e relida desde o inicio.
- Depois de alcancar o fim, rele os **ultimos 2 dias** ate a posicao, para pegar linhas
  lancadas depois com a mesma data. Como o `event_id` e deterministico, a releitura nao
  reenvia o que ja foi enviado.

## 4. Identidade do evento (`event_id`)

O `event_id` e um GUID deterministico (`ArpaEventIds`) calculado a partir de: instancia,
`entity_type`, `entity_key`, `occurred_at_utc`, **hash do payload** e, quando maior que 0, a
**geracao**. Consequencias:

- Reler a mesma linha sem mudanca gera o mesmo `event_id`. A fila local descarta o repetido
  (`ON CONFLICT (event_id) DO NOTHING`), e o ERP tambem.
- Uma mudanca com o mesmo `occurred_at` (ex.: titulo quitado no mesmo dia) muda o hash e gera
  evento novo.
- **Geracao da coleta incremental**: fixa por conexao
  (`collector.arpa.<conexao>.incremental_generation`). Ela e gravada na primeira coleta da
  1.6.59 com o valor que a coleta ja usava, entao a atualizacao nao mudou nenhum `event_id`.
- **Geracao do reenvio**: cada "Sincronizar tudo" incrementa
  `collector.arpa.<conexao>.resync_generation` e usa o valor novo. Com isso os eventos saem
  com IDs novos e o ERP reaplica, em vez de deduplicar.

Ate a 1.6.58 a coleta incremental usava a geracao do reenvio. Cada "Sincronizar tudo"
fazia a releitura dos ultimos 2 dias reenviar todas as entidades, inclusive as nao
marcadas.

## 5. Fila local (`sync_agent.outbox_events`)

| status | Significado |
|---|---|
| `pending` | Aguardando envio (ou nova tentativa apos `next_attempt_at_utc`) |
| `in_flight` | Num lote enviado e ainda sem resposta. Volta para `pending` depois de `InFlightRecoverySeconds` (900 s) se o agente cair no meio |
| `accepted` | Aceito pelo ERP |
| `rejected` | Reservado; o fluxo atual usa `dead_letter` para rejeicao |
| `dead_letter` | Recusado pelo ERP ou falha permanente. Uma copia vai para `sync_agent.dead_letter_events` |
| `resolved` | Dead-letter encerrado porque o mesmo dado ja foi aceito por um envio posterior (1.6.59, secao 10) |

`/status`, o Dashboard, a bandeja e o PDV mostram `pending_outbox_events` e
`dead_letter_events`, este ultimo contando so os **abertos**.

## 6. Envio ao ERP

`ErpEventDispatcher`, uma vez por ciclo:

1. Pega ate `BatchSize` eventos `pending` vencidos, na ordem de captura
   (`FOR UPDATE SKIP LOCKED`), e os marca `in_flight`. O padrao e 500 no `appsettings.json`
   e pode ser ajustado em Configuracoes > Arpa, painel "Envio ao ERP".
2. Faz `POST /v1/sync/events:batch` (timeout de 90 s), exigindo HTTPS fora da rede local.
3. Trata a resposta:
   - **HTTP 202**: todo evento fora de `rejected_events` vira `accepted`; os de
     `rejected_events` viram `dead_letter`, com o motivo dado pelo ERP.
   - **HTTP 400, 404, 409 ou 422** (falha permanente): o lote inteiro vai para `dead_letter`.
   - **Outros codigos, timeout ou rede** (falha transitoria): o lote volta para `pending`
     com espera exponencial (60 s, 120 s, ... ate 3600 s). Depois de `MaxAttempts` (8)
     tentativas vai para `dead_letter`.

Com milhares de eventos na fila, a fila drena um lote por ciclo. Um ciclo "concluido" com
pendentes e normal, e a barra de progresso da tela acompanha a drenagem.

## 7. Os botoes Sincronizar e Sincronizar tudo

### Sincronizar
Dispara um ciclo agora e abre o log ao vivo. A leitura continua de onde parou, pelo
marcador. Pode ser clicado quantas vezes quiser: os `event_id` saem iguais e nada duplica.
O painel "Sincronizar agora" permite restringir os tipos de dado so deste ciclo.

### Sincronizar tudo (reenvio)
Rele um periodo do Arpa e reenvia ao ERP. Serve para backfill e para corrigir dado errado
no ERP. Desde a 1.6.59:

**Periodo** (a meia-noite e a do fuso da maquina do agente, onde caem as colunas `date` do
Arpa):

| Opcao | De | Ate |
|---|---|---|
| Tudo | Inicio do historico | Momento do clique |
| Ultimos 3 meses | Meia-noite de 3 meses atras | Momento do clique |
| Ontem e hoje | Meia-noite de ontem | Momento do clique |
| Data especifica | Meia-noite da data **de** | Fim do dia da data **ate** (data ate = hoje vai ate o clique) |

**Funcionamento (job de reenvio):**
- O clique grava um job em `collector.arpa.<conexao>.resync_job`, com o periodo, os tipos, a
  geracao e um cursor por tipo.
- A cada ciclo, depois da coleta incremental, o coletor le o periodo de cada tipo a partir do
  cursor do job (ate 10 paginas por ciclo), e o trabalho sobrevive a reinicio do servico.
- **O marcador da coleta normal nao e tocado.**

**Trava por conexao:**
- Enquanto o job existir, outro "Sincronizar tudo" na mesma conexao e recusado (HTTP 409). O
  botao fica desabilitado e o status aparece abaixo dele.
- O job termina sozinho quando (a) a leitura de todos os tipos acabou e (b) nao ha mais
  evento do Arpa desses tipos em `pending` ou `in_flight` capturado desde o inicio. Dead-letter
  nao segura a trava.
- A trava e por conexao: um reenvio na loja ANP nao impede outro na BSB.
- Cliques simultaneos geram um unico reenvio, gracas a um semaforo no servidor e ao
  `INSERT ... ON CONFLICT DO NOTHING`.

**Protecoes:**
- O pedido e recusado com o coletor desativado ou com a conexao inativa, porque o job nunca
  avancaria.
- Um job parado mostra o motivo: conexao pausada, inativa ou coletor desativado.
- Um tipo que falha na leitura e tentado de novo. O reenvio so desiste dele depois de **3
  falhas seguidas durante pelo menos 30 minutos**, para que uma queda curta de rede nao o faca
  desistir.
- Ao terminar com tipos nao reenviados, o log mostra "concluido COM FALHAS". A linha da
  conexao mostra o que faltou e o motivo (`collector.arpa.<conexao>.resync_last`) ate o
  proximo reenvio concluido.
- **Cancelar reenvio** apaga o job: o que ja foi lido segue para o ERP e o resto do periodo
  nao e lido. Remover a conexao apaga o job e o ultimo resultado.

**Cuidados que continuam valendo:**
- Cada reenvio concluido gera um lote novo dos mesmos dados. Antes de reenviar, resolva a
  causa (ex.: produto que nao existe no ERP), senao o reenvio so gera mais dead-letter.
- Use o menor periodo e so os tipos necessarios.
- No ERP, o reenvio de venda nao duplica a venda nem movimenta estoque, mas sobrescreve edicoes
  feitas no ERP nas vendas reenviadas. Venda com devolucao falha no reenvio.

Endpoints: `POST /config/arpa/full-resync` (`id`, `since_preset`, `since_date`,
`until_date`, `entity_types`), `POST /config/arpa/cancel-resync` (`id`) e
`GET /config/arpa/resync-status` (`jobs[]`, `last_failures[]`).

## 8. Do ERP para o agente

- **Heartbeat** (`ErpHeartbeatClient`), a cada ciclo. O agente envia status, contagens,
  conectividade e `alerts[]` (avisos do painel; contrato 2.20.0). A resposta do ERP traz:
  - `license`: estado do plano;
  - `accepted_entity_types`: o que o ERP aceita;
  - `pending_update`: atualizacao do aplicativo;
  - `pending_resyncs`: re-sync sob demanda.
- **Re-sync sob demanda** (`ArpaResyncProcessor`): para cada pedido do ERP, o agente rele a
  linha na view por chave, ignorando o marcador, reenfileira com um `event_id` novo e confirma
  com `resyncs:ack`. Substitui o acesso direto do ERP ao banco do cliente nos fluxos de reparo.
- **Snapshots** para o PDV local: operadores, produtos, formas de pagamento e clientes.

## 9. Reconciliacao

A cada ciclo o agente resume as ultimas 24 h da fila (capturados, aceitos, rejeitados e
dead-letter, por entidade, com hash agregado) e envia ao ERP. O dead-letter **encerrado**
(`resolved`) continua contando como `dead_letter` nesse envio, de modo que o numero mandado ao
ERP nao muda com o encerramento.

## 10. Dead-letter

**O que e:** evento que o ERP recusou explicitamente, ou falha permanente. O agente nao tenta
de novo sozinho. O registro antigo nao se resolve sozinho, mesmo quando o dado ja chegou certo
por um envio posterior, porque cada envio tem o seu `event_id`.

**Tela Dead-letter** (`/dead-letter`, 1.6.59; link no contador do Dashboard e em
Configuracoes):
- Mostra abertos, superados e encerrados por origem, tipo e motivo. Sao no maximo 100 grupos,
  com totais exatos.
- **Superado** = existe envio posterior da mesma entidade aceito pelo ERP, com o mesmo
  `source_system`, `entity_type`, `entity_key` e `payload.loja_codigo`, e
  `captured_at_utc` maior. So entra a origem `arpa`; venda do PDV segue no **Reprocessar** do
  Dashboard.
- **Encerrar N superados**: muda o status para `resolved` e grava em `dead_letter_events`
  `resolved_at_utc` e `resolved_by_event_id` (o aceito mais recente). **Nada e apagado.**
  Botao e automatico rodam um de cada vez (advisory lock).
- **Encerrar superados automaticamente a cada ciclo**: opcional, desligado por padrao
  (`dead_letter.auto_resolve_superseded`).
- Instalacao anterior a 1.6.59 precisa antes da rotina **Habilitar encerramento de
  dead-letter** (Configuracoes > Manutencao do banco local, com credencial admin do Postgres
  local). Sem ela a tela so mostra a previa.

O que continua aberto sem superado exige corrigir a causa e reenviar. Exemplo: os vendedores
do catalogo recusados por ERP anterior a 0.0.169 se resolvem atualizando o ERP, rodando
"Sincronizar tudo" so de Cadastros e depois encerrando os superados.

## 11. Estado local em `sync_agent.agent_state`

| Chave | Conteudo |
|---|---|
| `collector.arpa.<conexao>.<entidade>.watermark` | Cursor da coleta incremental (`value`, `key`) |
| `collector.arpa.<conexao>.incremental_generation` | Geracao fixa do `event_id` da coleta incremental |
| `collector.arpa.<conexao>.resync_generation` | Ultima geracao de reenvio usada |
| `collector.arpa.<conexao>.resync_job` | Reenvio em andamento (e a trava) |
| `collector.arpa.<conexao>.resync_last` | Resultado do ultimo reenvio concluido (tipos nao reenviados) |
| `dead_letter.auto_resolve_superseded` | Encerramento automatico de dead-letter ligado ou desligado |

## 12. Diagnostico rapido

| Sintoma | Onde olhar |
|---|---|
| Nada novo chega ao ERP de uma conexao | Log do ciclo: `PAUSADA`? Rodar **Preparar views** / rotina indicada |
| Pendentes so crescem | `/status`: `last_error`, conectividade, token; log "Envio ao ERP" |
| Dead-letter alto | Tela `/dead-letter`: motivo por tipo; encerrar superados; corrigir a causa dos abertos |
| "Sincronizar tudo" nao libera | Status abaixo do botao: fase, pendentes, "parado: ..."; **Cancelar reenvio** se preciso |
| Reenvio terminou mas faltou um tipo | Aviso na linha da conexao (`resync_last`) e log "concluido COM FALHAS" |
| Venda sem financeiro / sem vendedor | Runbook de incidentes, secoes especificas |
| Venda de uma loja sobrescrita por outra | Runbook: "venda/compra de uma loja sobrescrita pela de outra loja" |

## 13. Historico recente

| Versao | Mudanca na sincronizacao |
|---|---|
| 1.6.57 | Financeiro com data da venda/baixa (nao o vencimento); cursor paginado `(occurred_at, entity_key)`; releitura de 2 dias; marcador no futuro descartado; hash do payload no `event_id` |
| 1.6.58 | Vendedor nas vendas e vendedores no catalogo (views `2026.11.24.3`; par ERP 0.0.169) |
| 1.6.59 | "Sincronizar tudo" vira job com periodo de/ate, trava por conexao, cancelar e desistencia controlada; "Ultimo dia" vira "Ontem e hoje" a partir da meia-noite; geracao fixa da coleta incremental; tela Dead-letter com encerramento de superados |
