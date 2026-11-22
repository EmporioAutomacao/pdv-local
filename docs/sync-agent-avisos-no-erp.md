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

A notificação no ERP ainda não foi testada ponta a ponta: depende do ERP 0.0.166 no demo.

A conexão Arpa "Geral" aponta para `192.168.0.4:5432`, que não é acessível na rede nova.
Todas as entidades falham com `Failed to connect`, e isso **não** aparece como aviso no
painel nem no ERP. Ver "Limite".

## Limite

- O agente que não alcança o ERP (sem internet, DNS etc.) não consegue avisar. Detectar
  "agente sem heartbeat há X minutos" é trabalho do ERP e ainda não foi feito.
- Falha de conexão com o banco do Arpa (host fora do ar ou inacessível) aparece só no log do
  ciclo. Ela não é um aviso do painel e, portanto, também não vai para o ERP.
