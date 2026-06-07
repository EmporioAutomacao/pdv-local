# Politica de Seguranca - Arpa Read-Only

Data: 31/05/2026

## Regra inegociavel

O SyncAgent **nunca grava dados no banco do Arpa**.

O fluxo permitido e somente:

```text
Arpa -> SyncAgent -> ERP
```

Nao existe fluxo permitido:

```text
ERP -> SyncAgent -> Arpa
SyncAgent -> Arpa
```

## Permissoes permitidas no Arpa

O usuario usado pelo SyncAgent no banco Arpa deve ter somente:

- `CONNECT` no banco;
- `USAGE` no schema de exportacao, quando aplicavel;
- `SELECT` nas views/tabelas estritamente necessarias para exportacao.

Permissoes proibidas:

- `INSERT`;
- `UPDATE`;
- `DELETE`;
- `TRUNCATE`;
- `CREATE`;
- `ALTER`;
- `DROP`;
- ownership de tabelas/schema;
- superuser;
- permissao administrativa.

## Views de exportacao

As views `sync_export.*` sao uma fronteira de leitura. Elas devem expor apenas
os dados necessarios para sincronizacao com o ERP.

Aplicar/criar views no Arpa e uma atividade de administracao do banco, fora do
runtime do SyncAgent. O usuario runtime do SyncAgent nao deve conseguir criar,
alterar ou remover views.

## Checklist obrigatorio

Antes de habilitar o collector:

1. Confirmar usuario read-only.
2. Confirmar que o usuario nao e dono de tabelas/views.
3. Confirmar que `INSERT`, `UPDATE`, `DELETE`, `CREATE`, `ALTER` e `DROP` foram negados.
4. Rodar preflight de permissao.
5. Rodar preflight das views.
6. Habilitar collector somente apos as validacoes.

## NO-GO

Nao habilitar o SyncAgent se:

- o usuario do Arpa tiver permissao de escrita;
- o usuario do Arpa for superuser;
- o usuario conseguir criar ou alterar objetos;
- o collector depender de tabela interna alem das views aprovadas;
- houver qualquer proposta de escrita no Arpa pelo SyncAgent.

## Bloqueio detectado no piloto Anapolis

Em 31/05/2026, a conexao Anapolis cadastrada no ERP foi diagnosticada usando
`postgres`, com superuser e permissoes de escrita. Essa credencial pode ser
usada apenas por DBA/admin para preparacao controlada, nunca como usuario
runtime do SyncAgent.

Se o Arpa legado aceitar `postgres` sem senha, isso e risco operacional do
ambiente e excecao temporaria de DBA. Essa condicao pode ser usada somente para
preparar views/usuario read-only quando explicitamente autorizada. Ela nunca
pode ser configurada no SyncAgent, instalador, tray, servico Windows, banco
local ou Control Plane como credencial runtime.

O runtime do SyncAgent deve usar usuario separado, por exemplo
`sync_agent_anapolis_ro`, com apenas `SELECT` nas views `sync_export`.

Scripts de preparacao como `prepare-arpa-anapolis-pilot.ps1` podem executar DDL
administrativo para criar views/usuario read-only, mas isso e atividade de DBA.
Esses scripts nao fazem `INSERT`, `UPDATE` ou `DELETE` em dados de negocio do
Arpa e nao devem ser executados pelo usuario runtime do SyncAgent.
