-- Contrato de views read-only esperado pelo SyncAgent no banco Arpa.
-- Ajuste os SELECTs conforme o schema real do Arpa antes de aplicar em homologacao.

CREATE SCHEMA IF NOT EXISTS sync_export;

-- View: sync_export.produtos
-- Colunas obrigatorias: entity_key text, occurred_at_utc timestamptz, payload_json text
-- Coluna opcional: trace_id text
-- Payload aceito: codigo, descricao, codigodefabrica, cod_ncm, codigodebarras,
-- codigo_barras, ean, gtin, ativo.
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
        END
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
