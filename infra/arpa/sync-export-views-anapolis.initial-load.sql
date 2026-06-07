-- SQL candidato gerado de diagnostico Arpa.
-- Arquivo de origem: D:\GitHub\erp\artifacts\arpa-schema-diagnostics-anapolis.json
-- Revisar antes de aplicar no Arpa real.
-- MODO CARGA INICIAL: entidade sem coluna temporal usa timestamp fixo.
-- Nao usar para sincronizacao continua sem definir uma fonte de alteracao.

CREATE SCHEMA IF NOT EXISTS sync_export;

CREATE OR REPLACE VIEW sync_export.produtos AS
SELECT
    "codigo"::text AS entity_key,
    TIMESTAMPTZ '2000-01-01 00:00:00+00' AS occurred_at_utc,
    jsonb_build_object(
        'codigo', "codigo",
        'descricao', "descricao",
        'codigodefabrica', "codigodefabrica",
        'cod_ncm', "cod_ncm",
        'codigodebarras', "codbarra",
        'ativo', CASE WHEN UPPER(CAST("ativo" AS TEXT)) IN ('0', 'FALSE', 'F', 'A', 'ATIVO') THEN true WHEN UPPER(CAST("ativo" AS TEXT)) IN ('1', 'TRUE', 'T', 'I', 'INATIVO') THEN false ELSE true END
    )::text AS payload_json,
    concat('arpa-produto-', "codigo")::text AS trace_id
FROM public."produtos"
WHERE "codigo" IS NOT NULL;

CREATE OR REPLACE VIEW sync_export.clientes AS
SELECT
    "codigo"::text AS entity_key,
    COALESCE("datacad"::timestamptz, TIMESTAMPTZ '2000-01-01 00:00:00+00') AS occurred_at_utc,
    jsonb_build_object(
        'codigo', "codigo",
        'nome', "nome",
        'documento', "cpf_cnpj",
        'email', "email",
        'telefone', "fone",
        'ativo', CASE WHEN UPPER(CAST("status" AS TEXT)) IN ('A', 'ATIVO', '1', 'TRUE', 'T', 'SIM', 'S') THEN true WHEN UPPER(CAST("status" AS TEXT)) IN ('I', 'INATIVO', '0', 'FALSE', 'F', 'NAO', 'N') THEN false ELSE true END
    )::text AS payload_json,
    concat('arpa-cliente-', "codigo")::text AS trace_id
FROM public."clientes"
WHERE "codigo" IS NOT NULL;

