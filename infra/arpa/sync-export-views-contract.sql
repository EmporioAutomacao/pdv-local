-- Contrato de views read-only esperado pelo SyncAgent no banco Arpa.
-- Ajuste os SELECTs conforme o schema real do Arpa antes de aplicar em homologacao.

CREATE SCHEMA IF NOT EXISTS sync_export;

-- View: sync_export.produtos
-- Colunas obrigatorias: entity_key text, occurred_at_utc timestamptz, payload_json text
-- Coluna opcional: trace_id text
-- Payload aceito: codigo, descricao, codigodefabrica, cod_ncm, codigodebarras,
-- codigo_barras, ean, gtin, ativo, precocusto, precovenda.
-- Nome/descricao so sao usados pelo ERP na CRIACAO do produto (nunca
-- sobrescrevem um produto ja existente). Custo e preco de venda sao sempre
-- sobrescritos pelo Arpa quando presentes — mesma regra do sync direto em
-- conexoes/services/arpa_produtos.py.
/*
CREATE OR REPLACE VIEW sync_export.produtos AS
SELECT
    p.codigo::text AS entity_key,
    COALESCE(p.updated_at_utc, p.created_at_utc, now())::timestamptz AS occurred_at_utc,
    jsonb_build_object(
        'codigo', p.codigo,
        'descricao', p.descricao,
        'codigodefabrica', p.codigo_fabrica,
        'cod_ncm', p.ncm,
        'codigodebarras', p.codigo_barras,
        -- Arpa legado: em produtos, ativo=0 costuma significar ativo.
        'ativo', CASE
            WHEN UPPER(CAST(p.ativo AS TEXT)) IN ('0', 'FALSE', 'F', 'A', 'ATIVO') THEN true
            WHEN UPPER(CAST(p.ativo AS TEXT)) IN ('1', 'TRUE', 'T', 'I', 'INATIVO') THEN false
            ELSE true
        END,
        'precocusto', p.precocusto,
        'precovenda', p.precovenda
    )::text AS payload_json,
    concat('arpa-produto-', p.codigo)::text AS trace_id
FROM public.produtos p
WHERE p.codigo IS NOT NULL;
*/

-- View: sync_export.clientes
-- Colunas obrigatorias: entity_key text, occurred_at_utc timestamptz, payload_json text
-- Coluna opcional: trace_id text
-- Payload aceito: codigo, codcliente, cliente_codigo, nome, razao_social,
-- cliente, fantasia, cnpj_cpf, cpf_cnpj, cnpj, cpf, documento, email,
-- email_principal, telefone, fone, celular, whatsapp, ativo.
/*
CREATE OR REPLACE VIEW sync_export.clientes AS
SELECT
    c.codigo::text AS entity_key,
    COALESCE(c.updated_at_utc, c.created_at_utc, now())::timestamptz AS occurred_at_utc,
    jsonb_build_object(
        'codigo', c.codigo,
        'nome', c.nome,
        'documento', c.cnpj_cpf,
        'email', c.email,
        'telefone', c.telefone,
        'ativo', CASE
            WHEN UPPER(CAST(c.status AS TEXT)) IN ('A', 'ATIVO', '1', 'TRUE', 'T', 'SIM', 'S') THEN true
            WHEN UPPER(CAST(c.status AS TEXT)) IN ('I', 'INATIVO', '0', 'FALSE', 'F', 'NAO', 'N') THEN false
            ELSE true
        END
    )::text AS payload_json,
    concat('arpa-cliente-', c.codigo)::text AS trace_id
FROM public.clientes c
WHERE c.codigo IS NOT NULL;
*/

-- View: sync_export.estoque
-- Colunas obrigatorias: entity_key text, occurred_at_utc timestamptz, payload_json text
-- Coluna opcional: trace_id text
-- Payload aceito: codigo_produto, produto_codigo, codigo, codigo_arpa,
-- loja_codigo, codigo_loja, loja, estoque_codigo, quantidade, estoqueminimo,
-- estoquemaximo, localizacao.
-- No Arpa Control legado, estoque normalmente fica na mesma tabela de
-- produtos (colunas quantidade/estoqueminimo/estoquemaximo/localizacao) -
-- ajuste o FROM/JOIN se o schema real do cliente guardar estoque em tabela
-- separada.
/*
CREATE OR REPLACE VIEW sync_export.estoque AS
SELECT
    p.codigo::text AS entity_key,
    COALESCE(p.updated_at_utc, p.created_at_utc, now())::timestamptz AS occurred_at_utc,
    jsonb_build_object(
        'codigo', p.codigo,
        'quantidade', p.quantidade,
        'estoqueminimo', p.estoque_minimo,
        'estoquemaximo', p.estoque_maximo,
        'localizacao', p.localizacao
    )::text AS payload_json,
    concat('arpa-estoque-', p.codigo)::text AS trace_id
FROM public.produtos p
WHERE p.codigo IS NOT NULL;
*/
