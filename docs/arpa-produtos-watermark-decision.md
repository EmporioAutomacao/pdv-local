# Decisao - Watermark de Produtos Arpa

Data: 30/05/2026

## Contexto

O diagnostico real das conexoes Arpa cadastradas no ERP encontrou:

- conexao `id=1` Anapolis;
- conexao `id=2` Brasilia;
- tabela de produto: `public.produtos`;
- codigo de produto: `codigo`;
- nome/descricao: `descricao`;
- codigo de barras: `codbarra`;
- coluna `ativo`, onde no legado `ativo=0` representa produto ativo;
- nenhuma coluna temporal confiavel de alteracao em `produtos`.

Para clientes:

- tabela: `public.clientes`;
- codigo: `codigo`;
- nome: `nome`;
- documento: `cpf_cnpj`;
- data de cadastro: `datacad`.

## Decisao para piloto

Para o piloto tecnico, `produto` sera tratado como **carga inicial controlada**.

Isso significa:

- a view `sync_export.produtos` pode usar um timestamp fixo em
  `occurred_at_utc`;
- o collector deve ser habilitado em janela controlada;
- apos a carga inicial, sync continuo de produto fica bloqueado ate existir uma
  fonte confiavel de alteracao;
- `cliente` pode usar `datacad` para carga inicial e novos cadastros, mas isso
  tambem nao garante alteracoes posteriores.

## O que fica proibido

- Rodar sync continuo de produtos usando timestamp fixo.
- Tratar `datacad` como coluna de atualizacao.
- Habilitar collector permanente sem resolver watermark.
- Aplicar views no Arpa real sem selecionar a conexao piloto.

## Caminhos para producao

Antes de producao, escolher uma alternativa:

1. criar coluna de atualizacao e trigger no Arpa;
2. usar log/tabela auxiliar confiavel de alteracoes;
3. executar reconciliacao/carga periodica completa com regra operacional
   explicita.

## Proxima decisao

Escolher qual conexao Arpa sera usada no primeiro piloto real:

- `id=1`: Anapolis;
- `id=2`: Brasilia.

