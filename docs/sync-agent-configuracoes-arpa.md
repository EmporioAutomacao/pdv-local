# Configuracoes > Arpa - Referencia da tela

Data: 05/09/2026

Referencia da aba **Configuracoes > Arpa** do dashboard local do Sync Agent
(`http://127.0.0.1:47891/config/arpa`). E onde o suporte/instalador liga o
coletor Arpa e cadastra as conexoes com o(s) banco(s) Arpa Control do
cliente, sem editar arquivo nem mexer no ERP.

Vale a partir de **1.6.2** (a tela existe desde 1.4.0; o botao de
ligar/desligar, o dropdown de Loja e a trava anti-duplicata sao de 1.6.2).

## O que a tela faz

```
Configuracoes > Arpa
├── Coletor Arpa: [Ativado|Desativado]   <- botao Ativar/Desativar coletor
├── Conexoes (tabela)                     <- uma linha por banco Arpa Control
│     Editar | Sincronizar | Remover
└── Adicionar / Editar conexao (form)
      Nome, Loja/Estoque (dropdown do ERP),
      Host/Porta/Database, Usuario/Senha (read-only), Batch size,
      toggles: Produtos / Clientes / Estoque / Vendas / Financeiro,
      "Controla o estoque desta Loja", "Ativa"
      [Salvar] [Testar conexao] [Limpar]
      > Preparar banco Arpa (requer credencial DBA)
          [Preparar views sync_export] [Criar usuario read-only]
```

## 1. Ligar/desligar o coletor (botao)

O botao **Ativar coletor** / **Desativar coletor** no topo da pagina liga e
desliga toda a coleta Arpa **na hora** - sem editar `appsettings.json`, sem
reiniciar o servico `AraraSuiteSync`.

- Enquanto desativado, **nenhuma conexao coleta** (o heartbeat, o envio de
  vendas do PDV e o dispatcher continuam normais).
- Desativar pede confirmacao.
- A preferencia fica em `arpa-collector-settings.json` (JSON simples, **sem
  segredo**: `{"enabled": true|false}`), no mesmo diretorio de secrets do
  agente (ao lado de `provisioning.dpapi`). Caminho derivado de
  `Provisioning:ProtectedFile`.

**Precedencia:** o valor do botao manda sobre `ArpaCollector:Enabled` do
`appsettings.json`. Enquanto o botao **nunca foi usado** naquela instalacao,
vale o `appsettings.json` (padrao `false`) - instalacoes antigas nao mudam de
comportamento so por atualizar. Uma vez usado o botao, o `appsettings.json`
deixa de importar para esse liga/desliga.

| Estado do arquivo | `appsettings.json` | Coletor |
|---|---|---|
| arquivo nao existe | `Enabled=false` | desligado (padrao) |
| arquivo nao existe | `Enabled=true` | ligado |
| `{"enabled": true}` | qualquer | **ligado** |
| `{"enabled": false}` | qualquer | **desligado** |

Cliente que **nao usa Arpa Control**: deixar desativado (botao, ou
`Enabled=false` se o botao nunca foi usado).

## 2. Conexoes

Cada linha da tabela e uma conexao com um banco Arpa Control, mapeada a uma
Loja/Estoque do ERP. Salvas **nesta maquina**, cifradas por DPAPI LocalMachine
em `arpa-connections.dpapi` (contem senha). Precedencia sobre
`ConnectionString`/`Entities` estaticos do `appsettings.json` e sobre
`UseRemoteConfig`.

Campos do formulario:

| Campo | Observacao |
|---|---|
| **Nome** | Rotulo livre da conexao (ex.: "Loja Centro"). Ao escolher a Loja no dropdown, e pre-preenchido com o nome dela se estiver vazio. |
| **Loja/Estoque (nome no ERP)** | Dropdown - ver secao 3. |
| **Host / Porta / Database** | Conexao Postgres do Arpa Control. |
| **Usuario / Senha** | Deve ser um usuario **read-only** (ver secao 4). Ao **editar** uma conexao a Senha vem em branco (nunca vai para o navegador) e assim fica: *Salvar* e *Testar conexao* usam a senha ja guardada. So digite para trocar. |
| **Batch size** | Linhas por lote na leitura das views (default 5000). |
| **Produtos / Clientes / Estoque / Vendas / Financeiro** | O que essa conexao sincroniza. O agente monta a query padrao contra `sync_export.<view>`. |
| ~~Cobranca~~ | `entity_type=cobranca` (contrato Sync 2.10.0) esta pronto no ERP e no agente (property `SyncCobranca`, normalizer, entity build), mas **ainda sem checkbox** no dashboard: falta a view `sync_export.cobranca` (schema de contas bancarias do Arpa varia muito — ver `infra/arpa/sync-export-views-contract.sql`). |
| **Controla o estoque desta Loja** | Quando marcado, eventos `estoque` desta conexao gravam o saldo na Loja; senao so cadastro. |
| **Ativa** | Desmarcar pausa so essa conexao, sem apagar. |

Botoes por linha: **Editar**, **Sincronizar**, **Sincronizar tudo**, **Remover**.

**Sincronizar** (1.6.6+) dispara um ciclo agora e abre um painel de **log ao
vivo** logo abaixo da tabela, mostrando linha a linha o que esta acontecendo:
por entidade (`Produtos: N lido(s), M novo(s)/alterado(s)`, idem
Clientes/Estoque/Vendas/Financeiro), o envio ao ERP (`aceitos`, `rejeitados`,
`falhas`), os snapshots que vem do ERP (operadores/produtos/pagamentos/clientes
do PDV) e o resultado final. Se ja houver uma sincronizacao em andamento, o
painel espera ela comecar. O botao **Fechar** aparece ao concluir.
Endpoint: `GET /config/arpa/sync-log` (JSON: `run_id`, `running`, `lines`).

**Sincronizar tudo** (1.6.9+) **zera os marcadores (watermarks)** desta conexao
e dispara um ciclo — a coleta re-le e re-envia **todo** o cadastro ao ERP
(produtos, clientes, estoque). Use para backfill ou para corrigir dados errados
no ERP. Pede confirmacao (pode gerar milhares de eventos; drenam a 500/lote).
Endpoint: `POST /config/arpa/full-resync` (`id`).

Botoes do formulario: **Salvar**, **Testar conexao** (valida credencial +
presenca das views `sync_export`), **Limpar**.

## 3. Loja/Estoque - dropdown do ERP (1.6.2+)

O campo *Loja/Estoque (nome no ERP)* e um **dropdown** com as Lojas/Estoque
realmente cadastradas no ERP do cliente, buscadas em
`GET /v1/sync/agents/{instanceId}/lojas` (contrato `../sync` 2.7.0). Antes era
texto livre e um typo criava uma Loja nova sem querer no ERP.

- O valor escolhido (o **nome** da Loja) vai como `loja_codigo` nos eventos de
  estoque/venda; o ERP resolve a Loja por esse nome
  (`sync_api.domain_processor`).
- Se o ERP estiver **fora do ar / instalacao nao ativada**, o dropdown cai
  para a opcao **"Outro (digitar manualmente)"** e um campo de texto - a tela
  nao trava.
- **Uma Loja/Estoque so pode estar em UMA conexao.** Salvar uma conexao
  apontando para uma Loja que ja esta em outra e recusado com **HTTP 409**:
  `A Loja/Estoque 'X' ja esta vinculada a conexao 'Y'`. Comparacao por nome,
  sem diferenciar maiusculas. Motivo: se duas conexoes escrevem estoque/venda
  na mesma Loja, o ERP nao sabe qual e a fonte. Um aviso ja aparece no
  formulario antes de salvar.

## 4. Preparar banco Arpa (requer credencial DBA)

Bloco recolhivel para o primeiro setup de uma conexao. Pede uma credencial de
**administrador do Postgres do Arpa**, usada **so naquele comando** e **nunca
gravada**. A **Senha DBA pode ficar em branco** se esse Postgres usa
`trust`/`peer` (comum em Arpa local - sem senha para o `postgres`); so o
**Usuario DBA** e obrigatorio. Se o servidor exigir autenticacao integrada do
Windows, o teste falha com mensagem explicando (o servico roda como LocalSystem
e nao consegue usar essa auth).

- **Preparar views sync_export** (1.6.9+): roda `infra/arpa/sync-export-views.sql`
  — um **script unico e generico** que **introspecta o schema real** do Arpa
  (nomes de tabela/coluna variam entre versoes) e cria `sync_export.produtos`
  (com preco), `.clientes` e `.estoque`. `occurred_at_utc` vem, em ordem: da
  tabela de log de alteracao (`alterados`/`alterados_clientes`/
  `produtos_altera_quantidade` -> sincronizacao **incremental** de verdade),
  senao de uma coluna temporal na propria tabela, senao timestamp fixo (carga
  inicial). `vendas`/`financeiro` so sao criadas se as tabelas padrao existirem
  (nao existem no Arpa Sistemas legado -> deixe esses toggles desmarcados).
  `sync_export.financeiro` cobre **contas a receber e a pagar** (o payload leva
  `natureza` = `receber` | `pagar`; o ERP grava `TituloReceber`/`TituloPagar`).
  O ramo de `contas_pagar` so entra se a tabela existir e, se o schema divergir
  do template, e pulado sem derrubar o de `contas_receber`.
  A **mesma passada** ja concede leitura das views ao Usuario da conexao. A
  mensagem lista o que foi criado e o que foi pulado. Nao precisa mais editar
  SQL por cliente.
- **Criar usuario read-only**: cria um role so-leitura com `GRANT SELECT` nas
  views, para usar em Usuario/Senha da conexao.

## Endpoints locais usados por esta tela

| Metodo | Rota local | Uso |
|---|---|---|
| GET | `/config/arpa` | HTML da tela |
| GET | `/config/arpa/lojas` | Lista de Lojas do ERP (proxy autenticado para `GET /v1/sync/agents/{id}/lojas`). `{ok:false}` quando offline/nao provisionado. |
| GET | `/config/arpa/sync-log` | Log ao vivo da ultima sincronizacao (`run_id`, `running`, `lines[]`). |
| POST | `/config/arpa/set-enabled` | `enabled=true|false` - grava `arpa-collector-settings.json` |
| POST | `/config/arpa/save` | Cria/edita conexao. **409** se a Loja ja estiver em outra conexao. |
| POST | `/config/arpa/delete` | Remove conexao (`id`) |
| POST | `/config/arpa/sync-now` | Dispara um ciclo |
| POST | `/config/arpa/full-resync` | Zera os watermarks da conexao (`id`) + dispara ciclo — re-envia tudo |
| POST | `/config/arpa/test` | Testa conexao |
| POST | `/config/arpa/prepare-views` | Roda o script unico de views + GRANT ao Usuario (credencial DBA no corpo) |
| POST | `/config/arpa/create-user` | Cria role read-only (credencial DBA no corpo) |

## Arquivos nesta maquina

| Arquivo | Conteudo | Cifrado |
|---|---|---|
| `arpa-collector-settings.json` | `{"enabled": bool}` - liga/desliga do botao | nao (nao e segredo) |
| `arpa-connections.dpapi` | lista de conexoes, **com senha** | DPAPI LocalMachine |

Ambos no diretorio de `Provisioning:ProtectedFile` (padrao:
`<InstallRoot>\Sync\Secrets\`).

## Ver tambem

- `infra/arpa/sync-export-views.sql` - o script unico e generico das views.
- `docs/arpa-collector-piloto.md` - usuario read-only, preflight, troubleshooting
  de erros de coleta.
- `docs/arpa-readonly-security-policy.md` - politica de acesso read-only.
- `docs/sync-agent-runbook-incidentes.md` - triagem de falhas.
- `../sync/openapi/erp-api-v1.yaml` - contrato do endpoint `/lojas`
  (`AgentLojasResponse`, 2.7.0).
