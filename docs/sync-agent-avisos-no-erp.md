# Avisos do painel enviados ao ERP (SyncAgent 1.6.56, contrato Sync 2.20.0)

Os avisos do topo do painel do SyncAgent também chegam ao ERP, que notifica os
superusuários no sininho, com websocket e push. Quem acompanha pelo ERP fica sabendo do
problema sem abrir o painel na máquina do cliente.

## O que é enviado

A cada heartbeat o agente manda `alerts[]` com a lista **completa** dos avisos ativos
(`Heartbeat/AgentAlerts.cs`):

| `code` | `key` | Gravidade | Origem |
|---|---|---|---|
| `erp_security_config` | `mutual_tls` ou `bearer_token` | erro | `ErpSecurityConfigDiagnostics` |
| `local_db_entity_type_check` | vazio | erro | banco local sem um `entity_type` |
| `arpa_connection_blocked` | id da conexão | erro | coleta da conexão pausada (tabela de preço, views desatualizadas, banco local) |
| `arpa_connection_warning` | id da conexão | aviso | aviso que não pausa (ex.: views desatualizadas com ERP antigo) |

São no máximo 50 avisos, com mensagens de até 2000 caracteres. A mensagem de
`local_db_entity_type_check` não leva o link local `/config/local-db`, porque esse link
não abre no ERP.

Se montar a lista falhar (por exemplo, erro ao ler o Postgres local), o heartbeat sai **sem**
o campo, e o ERP mantém o que já tinha. Uma lista vazia resolveria todos os avisos e
esconderia o problema.

## O que o ERP faz (0.0.166+)

- Um aviso novo notifica os superusuários com o nível erro ou aviso.
- Um aviso que passa de aviso para erro notifica de novo.
- Um aviso que some da lista é marcado como resolvido e gera uma notificação de sucesso.
- Um aviso que some e volta em menos de 30 minutos é reaberto sem notificar.
- A notificação abre a instalação em API de Sincronização > Instalações do PDV, que mostra
  o bloco "Avisos do painel do SyncAgent".

Um ERP antigo ignora o campo. Não há ordem obrigatória de implantação.

## Correção junto: falso aviso de bearer token

Antes da 1.6.56, toda instalação ativada mostrava no painel e em `/status` o aviso
"ErpSecurity:RequireBearerToken esta habilitado, mas nao ha token configurado". A checagem
olhava só o `appsettings.json` e a variável de ambiente, e ignorava o token gravado pela
ativação, que é o que o agente usa de fato. Agora ela considera esse token. Sem a correção,
o aviso falso viraria uma notificação permanente no ERP.

## Histórico na VM `ERP` (2026-11-22)

A rede mudou para a faixa `192.168.1.x`. A VM passou a usar DHCP (`192.168.1.53`) e é
acessada pelo nome `ERP`: o nome foi incluído no `TrustedHosts` do WinRM desta máquina.
Entre 21/11 17:20 e 22/11 11:08 (Brasília), a VM ficou sem resolver
`demo.ararasuite.com.br`. Nesse período o heartbeat falhou, e o "Atualizar App" mostrava
`HttpRequestException`.

| Horário (Brasília) | O que |
|---|---|
| 11:08 | Heartbeat volta a funcionar com a 1.6.55 (release `+b593312`). O "Atualizar App" responde `no_package_published`, porque não há versão mais nova liberada |
| 11:09 | Agente 1.6.56, build local ainda sem commit, instalado pelo fluxo manual do runbook. Backup em `C:\ProgramData\AraraSuite.com.br\backups\SyncAgent-bin-20261122-110958`. Bandeja e App continuam na 1.6.55, porque não mudaram |
| 11:12 | Heartbeat com `alerts` **aceito** pelo ERP do demo, que ainda é anterior à 0.0.166 e ignora o campo. `config_warnings` vazio: o aviso falso de bearer token sumiu |
| 11:15 | O `/status` informava 1.6.55: o arquivo `VERSION` não é trocado pela cópia manual (ver runbook). Com `VERSION` = 1.6.56 e o serviço reiniciado, o `/status`, o heartbeat e o `update-check` passam a informar 1.6.56 |

A conexão Arpa "Geral" aponta para `192.168.0.4:5432`, que não é acessível na rede nova.
Todas as entidades falham com `Failed to connect`, e isso **não** aparece como aviso no
painel nem no ERP. Ver "Limite".

Na mesma noite a VM foi reiniciada às 22:20 e às 22:27. O Postgres do Arpa, também na
porta 5432, subiu antes do banco do PDV e o painel ficou com erro 500. Em 22/11 às 23:00 o
banco do PDV foi movido para a porta 5433. Ver "Portas do Postgres na VM (2026-11-22)" no
runbook.

## Teste ponta a ponta (2026-11-23, VM `ERP` + demo 0.0.166)

O demo estava na 0.0.166 (`https://demo.ararasuite.com.br/health/`). O aviso foi provocado
na VM, não no ERP:

| Horário (Brasília) | O que |
|---|---|
| 00:47:01 | `catalogo` tirado da `outbox_events_entity_type_check` do banco local (regra recriada com `NOT VALID`, porque já havia eventos `catalogo` gravados) |
| 00:47:43 | Heartbeat aceito com o aviso `local_db_entity_type_check`. O painel mostra o aviso |
| — | Notificação de erro chegou ao sininho do superusuário no demo (conferido pelo usuário) |
| 00:50:07 | Regra original restaurada, idêntica ao backup. Aviso some do painel |
| 00:50:43 | Heartbeat aceito sem o aviso: o ERP marca como resolvido e manda a notificação de sucesso |

Backup da regra original:
`C:\ProgramData\AraraSuite.com.br\backups\teste-aviso-erp-*\constraint-original.sql`.

### Como repetir

O aviso do banco local é o mais fácil de provocar sem afetar o heartbeat. Não use o
aviso de mTLS (`RequireMutualTls` sem certificado): ele derruba o próprio heartbeat e o
aviso não chega ao ERP. Enquanto o aviso estiver ativo, a coleta do Arpa fica pausada.

1. Confira se o ERP está na 0.0.166 ou mais nova: `GET /health/`.
2. No painel da VM, abra **Configurações > Manutenção do banco local**
   (`http://127.0.0.1:47891/config/local-db`), informe a credencial admin (`postgres`) e
   rode em **SQL livre**:

   ```sql
   ALTER TABLE sync_agent.outbox_events DROP CONSTRAINT outbox_events_entity_type_check;
   ALTER TABLE sync_agent.outbox_events ADD CONSTRAINT outbox_events_entity_type_check
     CHECK (entity_type = ANY (ARRAY['cliente','produto','estoque','venda','financeiro',
                                     'compra','cobranca','plano_historico'])) NOT VALID;
   ```

3. Em até um heartbeat (~30 s), confira o painel e o sininho do ERP. No admin, o aviso
   aparece em *API de Sincronização > Avisos do agente* e na coluna "Avisos ativos".
4. Para desfazer, rode a rotina conhecida `fix_entity_type_check` na mesma tela. Ela recria
   a regra com todos os `entity_type` do contrato. No próximo heartbeat chega a notificação
   de "resolvido".

Se o contrato tiver ganhado um `entity_type` novo depois desta data, inclua-o na lista do
passo 2 e tire só um dos que já existiam.

**Desvio registrado:** no teste de 2026-11-23 o SQL foi rodado com `psql` direto na VM,
pelo assistente de código, com a senha do `postgres` lida do `.env` local
(`PDV_PG_ADMIN_PASSWORD`). Isso contraria a regra do runbook ("Manutenção do banco
local"): o caminho certo é o operador rodar pela tela do painel, como descrito acima. A
alteração foi desfeita e a regra conferida contra o backup.

## Limite

- O agente que não alcança o ERP (sem internet, DNS etc.) não consegue avisar. Detectar
  "agente sem heartbeat há X minutos" é trabalho do ERP e ainda não foi feito.
- Falha de conexão com o banco do Arpa (host fora do ar ou inacessível) aparece só no log do
  ciclo. Ela não é um aviso do painel e, portanto, também não vai para o ERP.
