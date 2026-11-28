# Runbook de Incidentes - Sync Agent

## Objetivo

Orientar suporte tecnico na triagem de falhas do Sync Agent entre PDV/local,
Arpa e ERP.

Funcionamento normal da sincronizacao, para comparar com o incidente:
[sync-agent-sincronizacao.md](sync-agent-sincronizacao.md).

## Fontes de diagnostico

Local no cliente:

```text
http://127.0.0.1:47891/
http://127.0.0.1:47891/status
http://127.0.0.1:47891/help
```

ERP:

```text
GET /v1/sync/agents/{instanceId}/status
Admin > Sync installations
Admin > Sync received events
Admin > Sync reconciliation runs
```

Banco local:

```sql
SELECT * FROM sync_agent.reconciliation_runs ORDER BY started_at_utc DESC LIMIT 5;
SELECT status, count(*) FROM sync_agent.outbox_events GROUP BY status;
SELECT entity_type, reason, count(*) FROM sync_agent.dead_letter_events GROUP BY entity_type, reason;
```

## Severidade

| Severidade | Condicao | Acao |
|---|---|---|
| SEV1 | perda confirmada de eventos, corrupcao de dados ou duplicidade de efeito de negocio | parar rollout, preservar banco local, acionar engenharia |
| SEV2 | agente offline por mais de 15 min, fila crescendo, reconciliacao divergente | atuar no mesmo dia, coletar evidencias |
| SEV3 | alerta pontual sem impacto operacional | acompanhar e registrar |

## Incidente: agente offline

Sinais:

- ERP retorna `connectivity=offline`;
- `/status` local nao responde;
- Windows Service parado.

Passos:

1. Verificar servico:

```powershell
Get-Service "AraraSuiteSync"
```

2. Iniciar se estiver parado:

```powershell
Start-Service "AraraSuiteSync"
```

3. Validar dashboard local:

```powershell
Invoke-RestMethod http://127.0.0.1:47891/status
```

4. Se falhar, coletar logs do Windows Event Viewer e preservar `pdv_sync`.

## Incidente: fila pendente crescendo

Sinais:

- `pending_outbox_events` aumenta continuamente;
- `oldest_pending_age_seconds` cresce;
- heartbeat pode estar `degraded`.

Passos:

1. Validar internet e DNS do endpoint ERP.
2. Validar `ErpApiBaseUrl`.
3. Validar token/certificado.
4. Executar sincronizacao manual:

```powershell
Invoke-RestMethod -Method Post http://127.0.0.1:47891/sync-now
```

5. Conferir `last_error` em `/status`.

### Padrao "desce e sobe o mesmo tanto, sem nunca drenar"

Caso real (2026-10-01): instalacao com `Pendentes` oscilando em torno do
mesmo valor (ex.: cai ~1000, sobe ~1000, de novo e de novo) a cada ciclo,
nunca drenando de verdade, junto com `Dead-letter` na casa de dezenas de
milhares e `Heartbeat: ok / degraded`.

Causa: o dispatcher reserva um lote inteiro (`ErpDispatcher:BatchSize` -
500/1000 por padrao) e muda o status pra `in_flight` **antes** de mandar -
isso ja tira aquele tanto do contador de `Pendentes`. Se a chamada HTTP pro
ERP falhar **como um todo** (timeout, erro de certificado mTLS, 5xx), o lote
inteiro volta pra `pending` (`ReleaseDispatchedEventsAsync`) e o contador
de `Pendentes` conta `status='pending'` sem olhar se o item esta em espera
de backoff - entao o numero sobe de volta na hora, mesmo que aqueles itens
so vao ser tentados de novo depois de minutos/horas. Resultado: parece que
"desce mil, sobe mil" pra sempre, mas na real e sempre o mesmo lote
entrando e saindo da reserva, com a causa de fundo (timeout/conectividade)
nunca resolvida.

Nota sobre `Heartbeat: ok / degraded`: esse campo fica `degraded` **pra
sempre** assim que `Dead-letter > 0` (`Worker.cs` `ResolveConnectivity`) -
nao e um indicador de conectividade em tempo real, e so "esta instalacao ja
teve DLQ alguma vez". Nao usar isso sozinho pra concluir que a conexao esta
ruim agora.

Diagnostico, antes de mexer em qualquer config:

```sql
SELECT entity_type, reason, count(*) FROM sync_agent.dead_letter_events GROUP BY entity_type, reason;
```

- Se a razao dominante for erro de timeout/conexao/certificado: o problema e
  o tamanho do lote e/ou o timeout da chamada (ver correcoes abaixo).
- Se for algo como `produto codigo_arpa=X not found`: e um problema de dado
  (ver secao "Incidente: dead-letter" abaixo) - reduzir lote/aumentar
  timeout nao resolve isso.

Correcoes pra timeout/lote grande demais:

1. **Reduzir o tamanho do lote** - ajustavel **sem reiniciar o servico**,
   direto no dashboard: Configuracoes > Arpa > secao "Envio ao ERP" > campo
   de tamanho do lote (1 a 5000) > Salvar. O ERP processa o lote inteiro
   **sequencialmente, dentro de uma unica transacao** (`sync_api.services.
   ingest_events_batch`), entao o tempo da chamada cresce proporcional ao
   tamanho do lote - um lote de 1000 pode estourar o timeout onde um de 500
   nao estouraria.
2. **Aumentar `ErpDispatcher:TimeoutSeconds`** - esse campo **nao tem
   controle no dashboard**, precisa editar o `appsettings.json` da
   instalacao e reiniciar o servico `AraraSuiteSync`:

```powershell
$path = "C:\Program Files\AraraSuite.com.br\Sync\Agent\appsettings.json"
$json = Get-Content $path -Raw | ConvertFrom-Json
$json.ErpDispatcher.TimeoutSeconds = 90
$json | ConvertTo-Json -Depth 20 | Set-Content $path -Encoding utf8
Restart-Service "AraraSuiteSync"
```

   O valor padrao do agente (codigo, `appsettings.json` shipado e o
   provisionado por `install-sync-agent.ps1`) foi elevado de **30s para
   90s** depois desse caso - 30s era curto demais pra um lote de 500-1000
   itens processado sequencialmente numa unica transacao no ERP, sem
   margem nenhuma. O piloto de Anapolis ja rodava com 120s sem problema
   registrado; 90s fica com folga do teto pratico de ~100s do proxy
   (Cloudflare) na frente do ERP. Instalacoes mais antigas, provisionadas
   antes dessa mudanca, continuam com 30s gravado no `appsettings.json`
   local ate alguem editar manualmente (o valor shipado so vale pra
   instalacao nova; auto-update nunca reescreve `appsettings.json`
   existente).
3. Nao assumir que so aumentar o timeout resolve: se a causa real for
   `RequireMutualTls` sem certificado (ver incidente especifico abaixo) ou
   um problema de dado (produto/cliente ausente), aumentar timeout so
   atrasa a mesma falha, nao evita ela.

## Incidente: dead-letter

O que significa um registro em dead-letter: e o estado **final e
permanente** de um evento - diferente de uma falha de conexao/timeout
(que volta pra `pending` pra nova tentativa automatica, ver secao acima),
dead-letter significa que o **ERP recebeu o evento e respondeu
explicitamente rejeitando** aquele `event_id` especifico (veio em
`rejected_events` na resposta do batch) - schema invalido, regra de negocio
quebrada (ex.: produto referenciado nao existe), ou `event_id` repetido com
`payload_hash` diferente. O agente nao tenta de novo sozinho nesse caso
(`MarkDispatchRejectedAsync`) - fica gravado com o motivo ate alguem agir.

**Importante - DLQ nao se autocorrige, nem quando o problema de fundo ja
sumiu**: cada tentativa de sincronizar uma mesma entidade (venda, produto,
etc.) gera um `event_id` proprio, calculado a partir de varios campos
(entidade, data do evento e, em caso de "Sincronizar tudo", o numero da
geracao de resync). Se uma venda falhar e virar dead-letter, e depois uma
sincronizacao posterior pegar essa mesma venda de novo e o ERP aceitar
dessa vez, isso acontece com um `event_id` **diferente** do que falhou -
sao dois registros completamente independentes aos olhos do sistema. O
registro antigo **continua marcado como dead-letter para sempre**, mesmo
que o dado em si (a venda) ja esteja correto no ERP via o evento novo que
deu certo. Reenviar manualmente o `event_id` identico tambem nao ajuda: o
ERP, ao ver um `event_id` que ja conhece com o mesmo conteudo, nem
re-executa a logica de negocio - so repete o motivo de rejeicao que ja
tinha salvo (`sync_api.services.ingest_events_batch`, branch de
`event_id`+`payload_hash` ja existentes). Por isso a contagem de
dead-letter e um **historico acumulado de tentativas que falharam em algum
momento**, nao um indicador confiavel de "quantos problemas existem hoje" -
pode (e costuma) ter bastante registro antigo ali que ja se resolveu
sozinho por outro caminho, sem que ninguem tenha "fechado" o registro
velho.

Sinais:

- `dead_letter_events > 0`;
- dashboard/tray indicam DLQ.

Passos:

1. Consultar motivos:

```sql
SELECT entity_type, entity_key, reason, failed_at_utc
FROM sync_agent.dead_letter_events
ORDER BY failed_at_utc DESC
LIMIT 50;
```

2. Se erro for payload invalido, corrigir normalizador/contrato antes de reprocessar.
3. Se erro for dependencia ausente no ERP, criar/corrigir entidade base e reprocessar manualmente com engenharia.
4. Antes de decidir reprocessar, verificar pelo `entity_key` (nao pelo
   `event_id`) se a entidade ja existe correta no ERP via um evento
   posterior que deu certo - se ja existir, o registro antigo pode ser
   arquivado/fechado como resolvido em vez de reprocessado.

   **A partir do SyncAgent 1.6.59** isso e a tela **Dead-letter**
   (`http://127.0.0.1:47891/dead-letter`, link no contador do Dashboard e em
   Configuracoes):
   - mostra abertos, **superados** e encerrados por origem, tipo e motivo;
   - **superado** = existe envio posterior aceito pelo ERP da mesma entidade
     (`source_system` + `entity_type` + `entity_key` + `payload.loja_codigo`,
     `captured_at_utc` maior). So entra `arpa`: venda do PDV segue no
     **Reprocessar** do Dashboard;
   - **Encerrar N superados** muda o status para `resolved` e grava em
     `dead_letter_events` `resolved_at_utc` e `resolved_by_event_id` (o aceito
     mais recente). **Nada e apagado**;
   - opcional, desligado por padrao: **Encerrar superados automaticamente a
     cada ciclo**;
   - instalacao anterior a 1.6.59 precisa antes da rotina **Habilitar
     encerramento de dead-letter** (Configuracoes > Manutencao do banco local),
     senao a tela so mostra a previa;
   - o contador `dead_letter_events` do painel/bandeja passa a contar so os
     abertos. A reconciliacao enviada ao ERP continua contando o encerrado como
     `dead_letter` (o numero enviado nao muda).
   O que continua aberto sem superado exige corrigir a causa e reenviar (ex.:
   vendedores do catalogo com ERP anterior a 0.0.169).
5. Nao existe hoje reprocessamento automatico nem em lote pra entidades
   vindas do Arpa (so ha um botao manual, por linha, pra vendas feitas no
   proprio PDV - `RequeueRejectedPdvSaleAsync`/`/pdv-sales/reprocess`).
   Construir isso exigiria mudanca nos dois lados (ERP precisaria de uma
   acao para resetar o `SyncReceivedEvent` antes de aceitar reprocessar o
   mesmo `event_id`; o agente precisaria filtrar por motivo pra nao
   reprocessar infinitamente erro de dado malformado, que nunca se resolve
   sozinho) - nao tratar como pendencia trivial.
6. Nunca apagar DLQ para "limpar painel" sem decisao tecnica. Encerrar um
   superado pela tela Dead-letter nao apaga nada e e o caminho para limpar o
   painel.

## Incidente: "Sincronizar tudo" usado mais de uma vez gera duplicacao de eventos

> A partir do SyncAgent 1.6.59, o agente **recusa** um segundo "Sincronizar tudo"
> na mesma conexao enquanto o primeiro estiver lendo do Arpa ou com eventos
> pendentes de envio ao ERP (HTTP 409; botao desabilitado). O reenvio tambem nao
> mexe mais no watermark da coleta incremental. O cuidado abaixo continua valendo
> entre reenvios **consecutivos**: depois que um termina, o proximo gera um lote
> novo. Para destravar um reenvio parado (ex.: conexao pausada), use **Cancelar
> reenvio** na linha da conexao.

"Sincronizar tudo" (full-resync, por conexao Arpa) **nao e idempotente** -
cada clique incrementa um contador de "geracao de resync"
(`BumpArpaResyncGenerationAsync`) que entra no calculo do `event_id` de
cada linha reenviada nesse ciclo. Isso e proposital (sem isso o reenvio
bateria na deduplicacao por `event_id` e nao mandaria nada de novo pro
ERP) - mas o efeito colateral e que **cada clique gera um lote de eventos
novo e distinto pros mesmos dados historicos**, sem substituir nem
deduplicar o(s) clique(s) anterior(es).

Risco concreto: se os dados por tras do resync ja tem um problema (ex.:
produto nao encontrado, erro de conectividade estrutural), cada clique em
"Sincronizar tudo" **nao corrige nada - so empilha mais linhas em
`pending`/`dead_letter` em cima do que ja existia**. Uma instalacao com
`Pendentes`/`Dead-letter` muito acima do volume normal de vendas/produtos
dessa loja e um sinal de que alguem pode ter clicado "Sincronizar tudo"
mais de uma vez tentando "forcar" uma correcao, sem resolver a causa raiz
antes.

O que **e** seguro clicar quantas vezes quiser: o botao comum
"Sincronizar agora" (incremental, nao mexe na geracao de resync) - os
`event_id` ficam iguais entre execucoes e a deduplicacao
(`ON CONFLICT (event_id) DO NOTHING`) absorve o reenvio sem duplicar nada.

Passos antes de usar "Sincronizar tudo" numa instalacao com fila presa:

1. Rodar a query de `dead_letter_events` (secao anterior) e resolver a
   causa raiz primeiro (timeout/certificado ou dado faltando no ERP).
2. So depois disso considerar um full-resync, e preferir o escopo mais
   estreito possivel (3 meses / 1 dia / data especifica) em vez de "Tudo",
   pra nao reenviar anos de historico de uma vez so.
3. Nunca usar "Sincronizar tudo" como tentativa repetida de "ver se agora
   funciona" sem checar o motivo do dead-letter entre uma tentativa e
   outra - cada clique sem diagnostico so aumenta o volume preso.

## Incidente: venda/compra de uma loja sobrescrita pela de outra loja

Sintoma: uma venda do Arpa some do ERP ou aparece com itens/estoque de outra
loja depois que a outra loja sincroniza. Acontece quando duas lojas tem bancos
Arpa proprios e repetem o mesmo codigo de venda/compra.

Causa: ERP anterior a 0.0.151 identificava a venda/compra do Arpa so pelo
codigo externo. Desde a 0.0.151 (contrato `../sync` 2.17.3) a chave e codigo
externo + Estoque da conexao, e o agente nao precisa mudar: o `loja_codigo` da
conexao ja vai em todo evento de venda/compra. Regra completa:
`erp/docs/infra/sync-agent-arpa.md`, secao "Identidade de venda e compra".

Correcao dos dados (depois de o ERP estar na 0.0.151 ou mais nova):

1. Conferir que cada conexao tem a sua Loja/Estoque certa no dashboard
   (Configuracoes > Arpa). Sem isso o reenvio grava no estoque errado.
2. Resolver antes os dead-letters de venda (secao "Incidente: dead-letter"):
   produto nao encontrado ou empresa/estoque nao resolvidos fazem o reenvio
   falhar de novo.
3. No dashboard do agente da loja que perdeu as vendas: *Sincronizar tudo*
   marcando so **venda, compra e financeiro**, com data personalizada um pouco
   anterior a primeira venda afetada (nao usar "Tudo"). Ver tambem o incidente
   anterior: cada clique gera um lote novo.
4. Opcional: repetir na outra loja para garantir que as vendas dela estao
   integras.
5. Validar no ERP uma venda que colidiu: dois registros com o mesmo codigo
   externo, um por estoque, cada um com os seus itens.
6. Venda que tem devolucao no ERP falha no reenvio (os itens devolvidos sao
   protegidos). Tratar uma a uma.

O reenvio nao duplica vendas da mesma loja (o ERP atualiza pelo codigo +
estoque) e nao movimenta estoque, mas sobrescreve edicoes feitas no ERP nas
vendas reenviadas.

## Incidente: vendas do Arpa chegam sem financeiro (watermark no futuro)

Sintoma: a venda aparece no ERP, mas a aba Financeiro dela fica vazia. A baixa
de titulos antigos tambem para de chegar. "Sincronizar tudo" resolve por um
tempo e o defeito volta.

Causa: views `sync_export` anteriores a `2026.11.24.2` usavam o vencimento
como `occurred_at_utc` do financeiro, e o coletor guardava o maior valor lido
como watermark. Bastava uma parcela que vence no futuro para o watermark ir
para o futuro e o coletor deixar de ler tudo o que aconteceu antes dessa data.
Corrigido no SyncAgent 1.6.57 (views `2026.11.24.2`) junto com o ERP 0.0.168,
que liga a venda ao titulo que chegou antes dela.

Diagnostico:
1. No banco local do agente:
   `SELECT state_key, state_value FROM sync_agent.agent_state WHERE state_key LIKE 'collector.arpa.%.financeiro.watermark';`
   Se `value` estiver depois de agora, e este incidente.
2. No Arpa: `SELECT max(occurred_at_utc), count(*) FILTER (WHERE occurred_at_utc > now()) FROM sync_export.financeiro;`
   Se a contagem for maior que zero, a view ainda e a antiga.

Correcao:
1. Atualizar o agente para a versao com `ArpaCollectionCursor`. Ela pagina por
   `(occurred_at_utc, entity_key)`, nunca le nem grava posicao no futuro e
   descarta sozinha um watermark que ja esteja no futuro, relendo a entidade
   uma vez. O `event_id` passa a incluir o hash do payload, entao uma baixa com
   a mesma data da venda tambem chega.
2. "Preparar views" na conexao (a versao nova das views pausa a coleta ate isso).
   Conferir depois: `SELECT pg_get_viewdef('sync_export.financeiro'::regclass) ~ '_nao_futuro';`
   deve dar `true` (antes de `2026.11.24.2` o re-preparo de um banco no esquema
   fallback mantinha a view antiga).
3. Conferir no ERP que os titulos chegaram e que a aba Financeiro das vendas
   novas mostra as parcelas. Se faltar titulo com o watermark ja no passado, use
   "Sincronizar" so o financeiro a partir de uma data anterior a primeira venda
   sem financeiro.
4. Titulos que ja estao no ERP sem venda ou com o cliente errado (gravados antes
   do ERP 0.0.168) sao corrigidos por "Sincronizar tudo" com **vendas** e
   **financeiro**, num periodo que comece antes das vendas com problema. O
   reenvio usa geracao nova no `event_id`, e o ERP reaplica o titulo, ligando a
   venda e o cliente. Ficam soltos, de proposito:
   - o titulo sem codigo de venda;
   - o titulo cuja venda nao existe no ERP naquele estoque.

   A consulta para medir antes e depois esta no ERP, na subsecao "Titulos a
   receber soltos".

Detalhes: `erp/docs/infra/sync-agent-arpa.md`, secao "Financeiro das vendas do
Arpa: watermark nunca no futuro".

## Incidente: reconciliacao divergente

Sinais:

- `remote_matched=false` em `sync_agent.reconciliation_runs.summary`;
- ERP `SyncReconciliationRun.matched=false`;
- endpoint central indica `connectivity=degraded`.

Passos:

1. Comparar `mismatches` no ERP.
2. Identificar entidade e campo divergente: `captured`, `accepted`, `rejected`, `dead_letter` ou `aggregate_hash`.
3. Se apenas `aggregate_hash` divergir com contadores iguais, comparar eventos por `payload_hash`.
4. Se `accepted` local maior que remoto, verificar lote aceito sem persistencia no ERP.
5. Se local menor que remoto, verificar reenvio antigo ou janela incorreta.

## Incidente: autenticacao ERP

Sinais:

- HTTP `401` ou `403`;
- heartbeat falha;
- dispatch entra em retry.

Passos:

1. Confirmar token provisionado no ERP.
2. Confirmar variavel de ambiente de maquina:

```powershell
[Environment]::GetEnvironmentVariable("PDV_SYNC_ERP_ACCESS_TOKEN", "Machine")
```

3. Confirmar certificado cliente quando mTLS estiver habilitado.
4. Rotacionar token com `provision_sync_agent --rotate-token` se houver suspeita de vazamento.

## Incidente: RequireMutualTls sem certificado provisionado

Caso real (2026-09-15): uma estacao com o mesmo sistema/ERP de outra que
funcionava normalmente mostrava, no botao **Atualizar App** da bandeja,
`500 Internal Server Error` (antes da 1.6.30) e depois, ja na 1.6.30, o
popup passou a mostrar o texto real: `Client certificate is required.
Provision ClientCertificateThumbprint or ClientCertificatePath in
ErpSecurity.` A causa era `ErpSecurity:RequireMutualTls=true` sem nenhum
certificado configurado nessa estacao especifica - **inclusive o
`appsettings.json` padrao do instalador ja vem com esse valor**, entao
qualquer instalacao que pule `provision-sync-agent-security.ps1 -PfxPath`
nasce nesse estado. `ValidateProvisionedForRemoteEndpoint` lanca essa
excecao pra **toda** chamada ao ERP (heartbeat, dispatch, snapshots,
update-check) - nao e so o botao de atualizar que quebra.

Sinais:

- `runtime_status=degraded` com `last_error` contendo `Client certificate
  is required...` (ou `Bearer token is required...`, mesma causa-raiz mas
  pro token);
- a partir de 1.6.31, aparece **antes** de qualquer falha real: `GET
  /status` traz `config_warnings` nao-vazio, e o dashboard (`/`) mostra um
  aviso amarelo no topo. Nao espere o popup de erro pra checar - olhe
  `config_warnings` primeiro.

Passos:

1. Checar `/status` (nao precisa admin, so leitura):

```powershell
Invoke-RestMethod http://127.0.0.1:47891/status | Select-Object config_warnings, runtime_status, last_error
```

2. Confirmar o valor real gravado no disco (o que o `/status`/`config_warnings`
   reflete e o que o processo tem carregado em memoria - confirme que bate
   com o arquivo, ja que reload de config so acontece se o arquivo mudar
   *enquanto* o servico roda):

```powershell
Get-Content "C:\Program Files\AraraSuite.com.br\Sync\Agent\appsettings.json" | Select-String "RequireMutualTls|RequireBearerToken|ClientCertificate"
```

3. Se este cliente **nao usa mTLS** (caso comum): editar o `appsettings.json`
   **com PowerShell/editor elevado** (gravar em `C:\Program Files\...` sem
   elevacao falha ou nao persiste, sem aviso claro - foi exatamente o que
   aconteceu no caso real acima, a primeira tentativa de edicao "sumiu"):

```powershell
$path = "C:\Program Files\AraraSuite.com.br\Sync\Agent\appsettings.json"
$json = Get-Content $path -Raw | ConvertFrom-Json
$json.ErpSecurity.RequireMutualTls = $false
$json | ConvertTo-Json -Depth 10 | Set-Content $path -Encoding utf8
Restart-Service "AraraSuiteSync"
```

4. Se este cliente **usa mTLS de verdade**: provisionar o certificado em vez
   de desabilitar a exigencia:

```powershell
.\infra\windows\provision-sync-agent-security.ps1 `
  -PfxPath "C:\Install\sync-agent-client.pfx" `
  -PfxPassword (Read-Host "Senha do PFX" -AsSecureString)
```

   e configurar `ErpSecurity:ClientCertificateThumbprint` no `appsettings.json`
   com o thumbprint exibido pelo script.
5. Confirmar que resolveu (`config_warnings` vazio, `runtime_status` fora de
   `degraded`):

```powershell
Invoke-RestMethod http://127.0.0.1:47891/status | Select-Object config_warnings, runtime_status, last_error, agent_version
```

## Incidente: ativacao pos-instalacao

Sinais:

- dashboard mostra `not_provisioned`;
- `/setup/activate` retorna erro;
- sincronizacao nao inicia apos instalacao.

Passos:

1. Confirmar que a URL do ERP esta correta.
2. Confirmar que a URL usa HTTPS fora de `localhost`/`127.0.0.1`.
3. Gerar novo codigo de ativacao no ERP se o codigo expirou ou ja foi usado.
4. Nao registrar o codigo de ativacao em chamado ou log.
5. Confirmar que `Provisioning:ProtectedFile` aponta para caminho absoluto no
   servico instalado.
6. Confirmar `/status`:

```powershell
Invoke-RestMethod http://127.0.0.1:47891/status
```

## Incidente: credenciais de provisionamento corrompidas

Sinais (agente < 1.6.48):

- `/status`, `/`, `/help` e `/update-check` retornam 500; a bandeja para de
  atualizar e o "Atualizar App" falha;
- Event Log `SyncAgent` a cada ~30s com `FormatException: The input is not a
  valid Base-64 string` em `ProvisioningStore.TryRead`.

Causa observada (VM de homologacao, 2026-10-14): queda de energia logo apos o
agente regravar `Sync\Secrets\sync-agent-provisioning.dpapi` numa rotacao do
refresh token - arquivo com o tamanho certo e conteudo zerado. Nao se recupera
sozinho.

A partir da 1.6.48 o agente move o arquivo ilegivel para
`sync-agent-provisioning.dpapi.corrupt-<data>` (log de erro no Event Log) e
volta como "nao ativado"; a gravacao tambem passou a forcar os dados para o
disco antes de substituir o arquivo.

Passos:

1. Atualizar para >= 1.6.48 (ou, em versao anterior: parar o servico, mover o
   `.dpapi` para fora da pasta e iniciar o servico).
2. No ERP, desativar a instalacao antiga e as conexoes Arpa espelhadas dela
   (senao o estoque continua vinculado a instalacao antiga e preco/estoque da
   nova sao ignorados).
3. Gerar novo codigo de ativacao e ativar em `/setup`.
4. Depois de ativado, o `.corrupt-*` pode ser apagado.

## Incidente: atualizacao pendente presa em confirmacao (contrato Sync 2.14.0)

Sinais:

- ERP mostra `pending_update_version` preenchido ha muito tempo, sem aplicar;
- bandeja mostra o item de menu **Atualizacao pendente...**, mas o usuario
  nao viu o balao ou fechou a janela sem decidir;
- `pending_update_deadline_at`/`pending_update_scheduled_at` no admin do ERP
  parecem no passado, mas a maquina nao atualizou.

Diagnostico:

1. Confirmar o estado local (arquivo na raiz da instalacao, nao na API —
   sobrevive a reinicios do servico):

```powershell
Get-Content "C:\Program Files\AraraSuite.com.br\pending-update-confirmation.json"
```

2. Confirmar o que a API local esta reportando (fonte usada pela bandeja):

```powershell
(Invoke-RestMethod http://127.0.0.1:47891/status).pending_update_confirmation
```

3. Se `awaiting_choice=true` e o prazo (`deadline_at_utc`/`scheduled_at_utc`)
   ja passou, o proximo heartbeat (ate 30s) deve aplicar sozinho — o
   `PendingUpdateConfirmationGate` reavalia a cada ciclo. Se nao aplicar
   depois de mais de 1 minuto do prazo vencido, confirmar que o servico
   `AraraSuiteSync` esta rodando e que o heartbeat esta tendo sucesso
   (`last_heartbeat_succeeded` em `/status`).

Passos para desbloquear manualmente:

- **Aplicar agora, sem esperar o prazo:**

  ```powershell
  Invoke-RestMethod http://127.0.0.1:47891/pending-update/confirm -Method Post
  ```

- **Reagendar para um horario melhor** (evita interromper o operador no meio
  de um atendimento):

  ```powershell
  Invoke-RestMethod http://127.0.0.1:47891/pending-update/schedule -Method Post `
    -ContentType "application/json" -Body '{"scheduled_at":"2026-09-23T20:00:00Z"}'
  ```

- **Cancelar o agendamento administrativo por completo** (se foi engano): no
  ERP admin, **Instalacoes do PDV** > abrir a instalacao > limpar
  `pending_update_version` manualmente, ou gerar um novo agendamento com a
  mesma versao instalada (o ERP auto-limpa quando `pending_update_version ==
  agent_version` no proximo heartbeat).

Nao apagar `pending-update-confirmation.json` a mao como primeira tentativa —
o ERP continua sendo a fonte de verdade (`pending_update.deadline_at`/
`scheduled_at` no heartbeat), o arquivo local so evita reperguntar ao usuario
e cobre janelas curtas offline. Apagar o arquivo faz o agente tratar o pedido
como novo e reenviar `update:ack action=presented` (reinicia a contagem dos 5
minutos) — util so se o arquivo estiver corrompido.

## Incidente: permissao read-only Arpa

Sinais:

- `runtime_status=degraded`;
- `last_error` contem `permission denied for relation produtos` ou
  `permission denied for relation clientes`;
- collector nao consegue ler `sync_export`.

Passos:

1. Nao trocar o SyncAgent para usuario `postgres`.
2. Revalidar permissoes do usuario runtime:

```powershell
$env:ARPA_SYNC_READONLY_PASSWORD = "<senha-runtime-read-only>"
.\infra\windows\test-arpa-readonly-permissions.ps1 `
  -PostgresHost 192.168.0.4 `
  -DatabaseName anapolis `
  -DatabaseUser ararasuite_sync_ro  # nome atual em 2026-09-11; confira em Config Arpa > Editar conexao
```

3. Se autorizado pelo DBA, reaplicar somente DDL/grants:

```powershell
.\infra\windows\prepare-arpa-anapolis-pilot.ps1 `
  -DbaUser postgres `
  -AllowEmptyDbaPassword `
  -ConfirmApply
```

4. Validar exportacao:

```powershell
.\infra\windows\test-arpa-export-preflight.ps1 `
  -PostgresHost 192.168.0.4 `
  -DatabaseName anapolis `
  -DatabaseUser ararasuite_sync_ro  # nome atual em 2026-09-11; confira em Config Arpa > Editar conexao
```

5. Acionar ciclo manual e confirmar `runtime_status=idle`.

## Incidente: conexao Arpa pausada pedindo "Preparar views"

Sinais:

- aviso no topo do dashboard e em `/status`: "A conexao Arpa "X" precisa de
  "Preparar views": as views sync_export do banco Arpa estao desatualizadas
  (instalada: ...; necessaria: ...)";
- selo "⚠ Preparar views (pausada)" na conexao, em Configuracoes > Arpa;
- log do ciclo: `Conexao "X": PAUSADA - ...`; nada novo daquela conexao chega ao ERP.

Causa: o agente foi atualizado com um script de views mais novo (marca
`ararasuite:sync_export_versao` no schema `sync_export`) e o banco Arpa ainda tem
as views anteriores, ou views criadas a mao, sem marca. So pausa quando o ERP ja e
compativel (heartbeat com `accepted_entity_types`, ERP 0.0.164+). Com o ERP antigo
e so aviso.

Passos:

1. Se o aviso pedir, atualize o ERP primeiro.
2. Clique no link **"Preparar views"** do aviso (dashboard ou balao da bandeja),
   ou em `http://127.0.0.1:47891/config/arpa` clique em **Editar** na conexao. O
   bloco "Preparar banco Arpa" abre com o motivo.
3. Informe o usuario DBA (senha em branco se trust/peer) e clique em **Preparar
   views sync_export**. **Nao** marque "Forcar".
4. Se aparecer o aviso do banco local, rode a rotina "Corrigir
   outbox_events_entity_type_check" (incidente abaixo).
5. Confirme no log do ciclo, que comeca sozinho logo depois, que a conexao nao
   esta mais `PAUSADA` e que "Cadastros (especies/condicoes/contas)" leu itens.
6. Confira a marca, so leitura, com o usuario da conexao:

```sql
SELECT obj_description(oid, 'pg_namespace') FROM pg_namespace WHERE nspname = 'sync_export';
```

   Deve devolver `ararasuite:sync_export_versao=<versao do agente>`.

## Incidente: entity_type nao permitido na fila local (23514)

Sinais:

- log do coletor mostra `<entidade>: falhou - 23514: a nova linha da relacao
  "outbox_events" viola a restricao de verificacao
  "outbox_events_entity_type_check"`;
- afeta tipicamente `cobranca`, `plano_historico` ou `catalogo` (entity_type
  mais recentes no contrato Sync: 2.10.0, 2.11.0 e 2.19.0). A partir do agente com
  o contrato 2.19.0 o problema tambem **pausa** a conexao, com o aviso
  "o banco local do agente nao aceita ...", em vez de so falhar a entidade;
- a view `sync_export.<entidade>` correspondente existe e le normalmente (sem
  `42P01`) - o erro acontece so ao tentar enfileirar localmente.

Causa: a instalacao foi criada (ou reinstalada) antes do agente ganhar
suporte a esse `entity_type`. A constraint `outbox_events_entity_type_check`
do Postgres local ficou presa na lista antiga porque `self-update.ps1` nunca
roda migracao de schema - so o instalador roda o init SQL, na instalacao.

Passos:

1. Abrir `http://127.0.0.1:47891/config/local-db` (Configuracoes >
   Manutencao do banco local).
2. Selecionar a rotina **"Corrigir outbox_events_entity_type_check"** no
   dropdown, informar usuario/senha admin do Postgres local (sugestao do
   instalador: `postgres`) e clicar **Testar conexao** antes de executar.
3. Executar a rotina selecionada.
4. Rodar uma sincronizacao manual e confirmar que a entidade nao aparece
   mais como `falhou` no log.
5. Confirmar a constraint atualizada (leitura, credencial normal do
   agente `pdv_sync` - nao precisa de admin para so conferir):

```powershell
psql -U ararasuite -h localhost -d ararasuite -c "SELECT pg_get_constraintdef(oid) FROM pg_constraint WHERE conname = 'outbox_events_entity_type_check';"
```

   Deve listar todos os `entity_type` do contrato atual (`cliente`,
   `produto`, `estoque`, `venda`, `financeiro`, `compra`, `cobranca`,
   `plano_historico`, `catalogo`). Contagem por entidade/status para fechar o
   diagnostico:

```sql
SELECT entity_type, status, count(*) FROM sync_agent.outbox_events GROUP BY entity_type, status ORDER BY entity_type, status;
```

Nao editar a constraint via `psql`/SQL solto fora dessa tela - a rotina ja
usa a lista de `entity_type` atual do contrato (`SyncContractValues`), e
fica registrada/repetivel para outras instalacoes com o mesmo problema.

## Incidente: ALTER negado em pdv.sale_items (42501)

Sinais:

- log mostra `42501: e necessario ser o dono da tabela sale_items` (ou
  `permission denied for table sale_items`);
- persiste mesmo apos atualizar a versao do SyncAgent - nao e um problema
  resolvido por reinstalar/atualizar o agente;
- a partir de 1.6.29 esse erro fica so em log (nao derruba mais o ciclo de
  publicacao de vendas do PDV); em versoes anteriores podia interromper
  `GetPendingPdvSalesAsync` e travar o envio de vendas ao ERP.

Causa: a tabela `pdv.sale_items` e criada pelo instalador com o usuario admin
do Postgres (`postgres`), e o agente roda com um usuario so-DML de proposito
(sem `ALTER TABLE`), mesmo padrao/motivo do incidente 23514 acima. O agente
tenta um self-heal (`ADD COLUMN IF NOT EXISTS` para `unit_label`,
`unit_external_key`, `unit_factor`) no primeiro ciclo apos o start, para
cobrir bases atualizadas por auto-update que nunca rodaram de novo o init
SQL - mas esse ALTER exige ser dono da tabela, e o usuario runtime nunca e.
Atualizar a versao do agente nao resolve sozinho: o auto-update nao roda
migracao de schema, so o instalador roda o init SQL (que ja inclui essas
colunas desde a origem, em instalacoes novas).

Passos:

1. Abrir `http://127.0.0.1:47891/config/local-db` (Configuracoes >
   Manutencao do banco local).
2. Selecionar a rotina **"Adicionar colunas de unidade em pdv.sale_items"**
   no dropdown, informar usuario/senha admin do Postgres local (sugestao do
   instalador: `postgres`) e clicar **Testar conexao** antes de executar.
3. Executar a rotina selecionada.
4. Confirmar as colunas (leitura, credencial normal do agente `pdv_sync` -
   nao precisa de admin para so conferir):

```powershell
psql -U ararasuite -h localhost -d ararasuite -c "\d pdv.sale_items"
```

   Deve listar `unit_label`, `unit_external_key` e `unit_factor`.
5. Rodar uma venda de teste no PDV (ou reiniciar o servico) e confirmar que
   o erro nao aparece mais no log e que a venda e publicada normalmente.

Nao editar `pdv.sale_items` via `psql`/SQL solto fora dessa tela pelo mesmo
motivo do incidente 23514 - a rotina fica registrada/repetivel para outras
instalacoes com o mesmo problema.

## Incidente: initdb falha com "Permission denied" ao criar/alterar o DataDirectory

Caso real (2026-09-15), maquina de dominio corporativo: o instalador (GUI ou
`install-postgresql17-local.ps1`) falhava sempre no mesmo ponto, mesmo com
elevacao UAC genuina confirmada (token nao filtrado, `whoami /groups` sem
"uso apenas para negar"):

```
initdb.exe : initdb: erro: nao pode criar diretorio "C:/Program Files/AraraSuite.com.br/PostgreSQL17/data": Permission denied
```

Diagnostico descartou, nessa ordem, ate sobrar a causa real: variavel de
ambiente sobrescrevendo config (nao era), antivirus/EDR de terceiros (so
Windows Defender padrao, sem CrowdStrike/SentinelOne/etc), Controlled Folder
Access do Defender (desligado), Mark-of-the-Web no `.exe` baixado (ausente),
privilegios de token ausentes (`whoami /priv` normal - `SeTakeOwnershipPrivilege`
etc. presentes-mas-desabilitados, como em qualquer sessao elevada padrao).

Causa raiz confirmada por reproducao direta: `initdb.exe` no Windows sempre
tenta travar as permissoes do `DataDirectory` para a conta especifica que o
executa (equivalente ao `chmod 0700` do Unix) - e essa etapa de **alterar**
permissoes (nao a de criar o diretorio) e a que falha, especificamente
quando o dono da pasta e o **grupo** `BUILTIN\Administradores` em vez da
**conta individual**. Isso acontece porque o Windows, por padrao, atribui
como dono o grupo Administradores (nao o usuario) quando um membro desse
grupo cria uma pasta nova sob `Program Files` - mesmo com UAC genuinamente
elevado. `initdb` sempre tenta se tornar dono exclusivo da conta que o
executa (nao do grupo), e a troca de dono/DACL e negada nesse descompasso.

Sinais:

- `initdb: erro: nao pode criar diretorio "...": Permission denied` (dono
  ainda e o grupo Administradores quando a pasta nao existia antes);
- ou, se a pasta ja existir (de uma tentativa anterior), a mensagem muda
  para `initdb: erro: nao pode mudar permissoes do diretorio "...":
  Permission denied` - mesma causa, so muda o texto conforme o initdb cria
  do zero ou so ajusta uma pasta ja existente;
- `(Get-Acl $DataDirectory).Owner` mostra `BUILTIN\Administradores` (o
  grupo) em vez de `DOMINIO\usuario` (a conta especifica).

Corrigido a partir de 1.6.34 (`infra/windows/install-postgresql17-local.ps1`):
o script agora cria o `DataDirectory` explicitamente e forca o dono para a
identidade do processo atual (`[System.Security.Principal.WindowsIdentity]::GetCurrent().Name`)
antes de chamar `initdb`, em vez de deixar o `initdb` criar a pasta sozinho
e herdar o dono padrao do Windows (o grupo).

Correcao manual (instalacoes com o agente anterior a 1.6.34, ou se acontecer
de novo em algum outro caminho nao coberto pelo script): apagar a pasta
parcial e recriar forcando o dono:

```powershell
$dataDir = "C:\Program Files\AraraSuite.com.br\PostgreSQL17\data"
Remove-Item $dataDir -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $dataDir | Out-Null
$acl = Get-Acl $dataDir
$acl.SetOwner([System.Security.Principal.NTAccount]"$env:USERDOMAIN\$env:USERNAME")
Set-Acl $dataDir $acl
```

Depois rodar a instalacao de novo (ou so o `install-postgresql17-local.ps1`).

### Sintomas seguintes na mesma maquina (apos corrigir o initdb)

Numa maquina com varias tentativas de instalacao anteriores (comum em
desenvolvimento/homologacao), corrigir o initdb pode so revelar os proximos
dois problemas, ambos ja corrigidos a partir de 1.6.35:

**`Copy-Item : O processo nao pode acessar o arquivo '...\icudt67.dll'
porque ele esta sendo usado por outro processo.`** ao copiar os binarios
PostgreSQL - ha um `postgres.exe` **ja rodando** a partir do mesmo
`$InstallRoot` (reinstalacao/upgrade em cima de uma instalacao anterior,
possivelmente com outro nome de servico). `install-postgresql17-local.ps1`
agora para qualquer servico Postgres (por caminho do binario, nao por nome -
o nome pode ter mudado entre instalacoes) e qualquer `postgres.exe` orfao
rodando desse `InstallRoot` antes de copiar. Sem isso, a copia falha
parcialmente e a instalacao segue com binarios misturados (antigos +
novos).

**`ERRO: role "araras" nao existe`** (ou `role "pdv_sync" nao existe`,
dependendo de quando a instalacao original foi feita) durante "Applying
pgvector extension and sync_agent schema..." - `infra/postgres/init/00-set-timezone.sql`
tinha os nomes de banco/role **fixos** (`pdv`/`araras`, de antes de qualquer
renomeacao desta base de codigo - nem batia mais com o default atual do
instalador Windows nem com o `docker-compose.yml` de desenvolvimento).
Reescrito para descobrir banco e role dinamicamente em runtime
(`current_database()` + dono do banco via `pg_catalog.pg_database.datdba`) -
funciona com qualquer nome de banco/usuario, presente ou futuro, sem
precisar editar esse arquivo de novo numa proxima renomeacao. Validado
rodando contra um Postgres descartavel no Docker com nomes arbitrarios
(`testdb`/`testuser`).

## Incidente: vendas do Arpa chegam sem vendedor

Sintoma: no ERP a venda do Arpa fica sem vendedor e o quadro "Vinculo de vendedores" da
Conexao Arpa nao mostra o nome do vendedor (so o codigo) ou nao lista nada.

Causa: views `sync_export` anteriores a `2026.11.24.3` mandavam so `vendedor_codigo`, sem nome
nem documento, e nao traziam o cadastro de vendedores no catalogo. No ERP anterior a 0.0.169 o
vendedor nem era registrado na conexao.

Corrigido no SyncAgent 1.6.58 (views `2026.11.24.3`) junto com o ERP 0.0.169 (publicar o ERP
antes: ERP antigo recusa `catalogo` com `tipo=vendedor`).

1. Atualizar o agente e rodar **Preparar views** na conexao.
2. Conferir no banco Arpa (somente leitura) que a venda traz o nome:
   ```sql
   SELECT payload_json::jsonb->>'vendedor_codigo', payload_json::jsonb->>'vendedor_nome'
   FROM sync_export.vendas ORDER BY occurred_at_utc DESC LIMIT 20;
   ```
   Nome vazio com codigo preenchido: a tabela de cadastro do Arpa daquele cliente nao e
   `vendedores`/`vendedor`/`funcionarios`/`funcionario` (ou nao tem `codigo`/`nome`) - levantar
   com `export_arpa_schema_diagnostics` e ajustar os candidatos no script.
3. No ERP, vincular os vendedores na conexao e usar **Reaplicar vinculos nas vendas**.

Estrutura conferida em Anapolis (2026-11-24): `cabecalho_ordem_servico.vendedor` ->
`vendedores(codigo, nome, cpf, status)`; `status` true = ativo.

Sinal de que o ERP ainda nao e 0.0.169: dead-letter de `catalogo` com o motivo
`catalogo payload requires tipo (especie|condicao|conta) and codigo`, um por vendedor. Na VM
`ERP` (2026-11-25, ERP demo) foram 47 por vez. Ate o SyncAgent 1.6.58, cada "Sincronizar tudo"
reenviava esses 47 de novo pela releitura da coleta incremental. A partir da 1.6.59 isso nao
acontece mais (geracao da coleta incremental fixa). Atualizar o ERP resolve a causa, e os
registros antigos de dead-letter continuam la (ver "Incidente: dead-letter").

## Incidente: vendas do Arpa chegam com hora 00:00

Sintoma: no ERP a venda do Arpa aparece com a data certa e a hora 00:00.

Causa: views `sync_export` anteriores a `2026.11.25.1` mandavam em `data` so a coluna de data
do cabecalho. No Arpa de Anapolis ela e `cabecalho_ordem_servico.dataordem`, do tipo `date`, e o
ERP grava uma data sem hora como meia-noite. O contrato ja pedia `data` como timestamptz.

Corrigido no SyncAgent 1.6.60 (views `2026.11.25.1`), sem par de ERP. A view junta a data com a
coluna de hora do cabecalho, escolhida por nome (`horaordem`, `hora_ordem`, `hora`, `horario`,
`hora_venda`, `hora_abertura`, `hora_emissao`, `hora_cadastro`, `hr_ordem`, `hora_os`) ou, sem
nenhum desses nomes, pela primeira coluna do tipo `time` da tabela. A hora pode ser `time`,
`timestamp` (inclusive o estilo Delphi `1899-12-30 HH:MM:SS`) ou texto `HH:MM[:SS]`; texto
invalido cai na data pura sem derrubar a view. O `occurred_at_utc` da venda passa a usar a mesma
data e hora.

Venda fechada usa a data e a hora do **fechamento** quando a tabela tem o par
(`data_fechamento`/`data_finalizacao`/`data_encerramento` + `hora_fechamento`/`hora_finalizacao`/
`hora_encerramento`) e os dois estao preenchidos; venda aberta fica com a abertura acima. Em
Anapolis (conferido em 2026-11-26) a tabela tem `dataordem` (date), `hora_abertura`,
`data_fechamento` e `hora_fechamento` (time), e a hora de abertura se repete entre vendas
diferentes, por isso vale o fechamento. A abertura (`dataordem` + `hora_abertura`) vai junto, no
campo `data_abertura` (contrato Sync 2.22.0). No ERP 1.0.0 `data` grava em `Venda.data_fechamento`
(listagem, relatorios, DRE, comissoes) e `data_abertura` em `Venda.data_abertura` (detalhe da venda);
ERP anterior ignora `data_abertura`. O offset do `data` segue o fuso da sessao do Postgres
(um 9.6 com tzdata antigo mostra `-02:00` em horario de verao); o instante e o mesmo e o ERP
converte pelo offset.

1. Atualizar o agente e rodar **Preparar views** na conexao. A mensagem do Preparar views diz a
   coluna usada: `sync_export.vendas criada (fallback dinamico, tabela ..., data dataordem, hora X,
   fechamento data_fechamento + hora_fechamento)`.
   Com `hora sem coluna de hora`, levantar o nome real com `export_arpa_schema_diagnostics` e
   incluir nos candidatos do script.
2. Conferir no banco Arpa (somente leitura):
   ```sql
   SELECT entity_key, occurred_at_utc, payload_json::jsonb->>'data'
   FROM sync_export.vendas ORDER BY occurred_at_utc DESC LIMIT 20;
   ```
   `data` deve trazer a hora da venda com offset.
3. A coleta relê os ultimos 2 dias, entao essas vendas se corrigem sozinhas. Para as mais
   antigas, usar **Sincronizar tudo** com o periodo desejado. O ERP atualiza a venda existente
   (mesma chave codigo + loja).

## Evidencias obrigatorias

Coletar antes de qualquer correcao destrutiva:

- print ou JSON de `/status`;
- `instance_id` e `tenant_id`;
- ultimas 5 linhas de `sync_agent.reconciliation_runs`;
- contagem por status de `sync_agent.outbox_events`;
- amostra de `sync_agent.dead_letter_events`;
- status central no ERP;
- horario local e timezone do banco.

## Regras de seguranca

- Nao compartilhar Bearer token em chamados.
- Nao copiar PFX/certificado para canais de suporte.
- Nao remover banco local sem backup e autorizacao.
- Nao fazer `DELETE` em outbox, DLQ ou reconciliation sem plano de recuperacao.
