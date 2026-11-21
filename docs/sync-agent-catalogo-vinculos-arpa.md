# SyncAgent: cadastro do Arpa e vínculos da Conexão Arpa (contrato Sync 2.19.0)

Par no ERP: 0.0.163 (vínculos por lista de escolha) e 0.0.164 (`accepted_entity_types` no
heartbeat). Detalhes do lado do ERP: `erp/docs/modulos/arpa/vinculos-conexao.md`.

## O que o agente passou a enviar

O ERP traduz os códigos do Arpa pelos vínculos da Conexão Arpa e nunca pelo nome. A estrutura foi
levantada no Arpa real de Anápolis, só com leitura, em 2026-11-20.

| Campo | Origem no Arpa |
|---|---|
| venda `condicao_pagamento` / `condicao_descricao` | `cabecalho_ordem_servico.condicao_pgto` → `vencimentos` |
| venda `especie_descricao` | `especies.descricao` da espécie da venda |
| receber `especie_pagamento` / `especie_descricao` | `especie_parcela` da baixa (a de maior valor) |
| receber `conta_bancaria` / `conta_bancaria_nome` | `lctbancarios` (`parcela`) → `ctabancarias`. Sem lançamento bancário, se houver `caixa.parcela`, vai `CAIXA` |
| pagar `especie_pagamento` / `especie_descricao` | `apagar_especie` |
| pagar `conta_bancaria` / `conta_bancaria_nome` | `lctbancarios` com `origem = 4` e `item_origem` = título |

Ficam de fora:
- `especies.destino`, que está vazio no cadastro;
- a tabela `caixa` na baixa **a pagar**, porque a ligação por `item_origem` é ambígua ali.

## Novo tipo `catalogo`

A view `sync_export.catalogos` (em `infra/arpa/sync-export-views.sql`) traz o cadastro de
`especies`, `vencimentos` e `ctabancarias`, mais a conta `CAIXA`:
- `entity_key` é `<tipo>:<codigo>`, e o payload é `{tipo, codigo, descricao, ativo}`;
- é coletada sempre que a conexão tem Vendas ou Financeiro, sem toggle próprio
  (`Collectors/ArpaStandardEntities.cs`);
- `occurred_at_utc` é o início do dia, então o cadastro inteiro é reenviado uma vez por dia;
- o agente injeta o `loja_codigo`, para o ERP achar a conexão certa.

Em Anápolis são 103 itens: 77 condições, 18 espécies e 8 contas.

## Proteção contra agente novo com ERP antigo (`Heartbeat/ErpCompatibility.cs`)

O ERP 0.0.164 ou mais novo responde `accepted_entity_types` no heartbeat. O agente guarda a lista
em `sync_agent.agent_state` (chave `erp.compatibility`), então ela continua valendo depois de
reiniciar. Enquanto o ERP não declarar `catalogo` (ERP antigo não manda a lista, e antes do
primeiro heartbeat o agente também considera que não aceita):

- **coleta:** `catalogo` é pulado, com a mensagem "aguardando o ERP ser atualizado" no log do
  ciclo. Sem isso, o ERP rejeitaria os eventos e o cadastro viraria dead letter todo dia;
- **"Preparar views":** recusa e explica o motivo. As views novas mandam o código do Arpa, e o ERP
  antigo grava esse código como Espécie/Condição nova pelo nome ("17", "26"…). A opção "Forçar
  mesmo com o ERP desatualizado" existe e não é recomendada;
- **"Testar conexão":** não cobra a view `catalogos`;
- **"Corrigir outbox_events_entity_type_check":** fica inofensivo em qualquer ordem, porque o
  catálogo nem é coletado.

Depois que o ERP for atualizado, tudo é liberado sozinho no próximo heartbeat.

## Versão das views e pausa automática

O script grava a própria versão no banco do Arpa. A última instrução de
`infra/arpa/sync-export-views.sql` é
`COMMENT ON SCHEMA sync_export IS 'ararasuite:sync_export_versao=AAAA.MM.DD.N'`. Suba esse número
sempre que uma mudança no script exigir preparar as views de novo.

A cada ciclo, antes de coletar uma conexão, o coletor (`ArpaCollector.EvaluateDependenciesAsync` +
`Provisioning/ArpaDependencyRule.cs`) compara:
- a versão gravada (`ArpaDdlRunner.GetInstalledViewsVersionAsync`, que só lê `pg_namespace`
  e funciona com o usuário somente-leitura);
- a versão do script embutido no agente (`ArpaDdlRunner.EmbeddedViewsVersion`).

| Situação | Resultado |
|---|---|
| Views sem marca ou mais antigas, ERP compatível (0.0.164+) | **Pausa a coleta da conexão**, sem tocar na marca d'água, e retoma de onde parou depois de "Preparar views" |
| Views sem marca ou mais antigas, ERP antigo | **Só aviso**: a coleta continua no formato atual ("atualize o ERP e depois prepare as views") |
| Banco local sem um tipo que a conexão coleta | **Pausa** até "Corrigir outbox_events_entity_type_check" |

Onde aparece:
- aviso no topo de todas as páginas do painel e em `/status` (`config_warnings`,
  `arpa_blocked_connections` com `kinds`, `arpa_connection_warnings`);
- selo "⚠ Preparar views (pausada)" na linha da conexão;
- ao editar a conexão, o bloco "Preparar banco Arpa" abre sozinho com o motivo. O bloco ganhou
  destaque nos temas claro e escuro;
- log do ciclo: "PAUSADA - ...".

Heartbeat, envio da fila e PDV continuam. Depois de um "Preparar views" bem-sucedido, um ciclo
novo começa na hora.

Views criadas à mão (scripts `infra/windows/*sync-export-views*`) não têm a marca, então **pausam**
quando o ERP for compatível, até rodar "Preparar views". O Arpa de Anápolis está nessa situação
hoje (consultado só com leitura em 2026-11-20).

## Implantação

1. ERP 0.0.164 publicado e clientes atualizados.
2. SyncAgent **1.6.54** ou mais novo, a primeira versão com a pausa por views desatualizadas. A 1.6.55 acrescenta o link "Preparar views" nos avisos e o botão ocupado.
   A 1.6.53 (`+dcb8a04`) tem o catálogo e a proteção contra ERP antigo, mas não tem a pausa.
3. Em cada instalação: "Preparar views", com credencial DBA, e depois "Corrigir
   outbox_events_entity_type_check" (Manutenção do banco local).

## Histórico na VM 192.168.0.184 (2026-11-20)

A conexão "Geral" da VM lê o Arpa **real** de Anápolis e envia ao ERP do demo.

| Horário | O que |
|---|---|
| 17:58 | Agente com o catálogo, **antes** da proteção (sessão `pdv-local`) |
| 19:00 | 1.6.53 (`+dcb8a04`): catálogo e proteção contra ERP antigo |
| 20:47 | 1.6.54 (`+97f374b`). Depois de um ciclo, "Geral" ficou **PAUSADA** pedindo "Preparar views" (instalada: sem versão; necessária: 2026.11.21.1). Isso confirmou que o ERP do demo já declara `catalogo` |
| depois das 20:47 | "Preparar views" rodado no painel. O Arpa de Anápolis passou a ter a marca `2026.11.21.1` e a view `sync_export.catalogos` |
| 21:04 | Agente **e bandeja** 1.6.55 (`+f3fc520`). Bandeja reaberta na sessão do usuário `suporte`. Conexão sem pausa, 0 pendentes, dead letters inalteradas (135), ciclo sem erro, etapa "Cadastros" rodando |

Backups dos binários anteriores ficam em `C:\ProgramData\AraraSuite.com.br\backups\SyncAgent-bin-*` e
`SyncTray-bin-*`. Para atualizar a bandeja, veja `development-structure-and-vm-runbook.md`, seção
"Publicar nova build da bandeja (SyncAgent.Tray) na VM".

Ainda não confirmado: se os 103 itens do cadastro já chegaram ao ERP do demo. O ciclo das 21:08 leu
0 porque o cadastro vai uma vez por dia. Confira os quadros da Conexão Arpa no ERP.

Testes: `tests/sync-agent-tests/ErpCompatibilityTests.cs`, `ArpaStandardEntitiesTests.cs`,
`ArpaDependencyRuleTests.cs` e `ArpaDependencyHtmlTests.cs`.
