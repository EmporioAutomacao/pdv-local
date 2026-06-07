-- Fixture local para validar o preflight e o coletor sem depender do Arpa real.
-- Nao usar este arquivo em producao.

CREATE SCHEMA IF NOT EXISTS sync_export;

CREATE OR REPLACE VIEW sync_export.produtos AS
SELECT
    'PILOTO-PROD-001'::text AS entity_key,
    (now() AT TIME ZONE 'UTC')::timestamptz AS occurred_at_utc,
    jsonb_build_object(
        'codigo', 'PILOTO-PROD-001',
        'descricao', 'Produto Piloto Sync',
        'codigodefabrica', 'FAB-PILOTO',
        'cod_ncm', '12345678',
        'codigodebarras', '789000000001',
        'ativo', true
    )::text AS payload_json,
    'fixture-produto-piloto-001'::text AS trace_id;

CREATE OR REPLACE VIEW sync_export.clientes AS
SELECT
    'PILOTO-CLI-001'::text AS entity_key,
    (now() AT TIME ZONE 'UTC')::timestamptz AS occurred_at_utc,
    jsonb_build_object(
        'codigo', 'PILOTO-CLI-001',
        'nome', 'Cliente Piloto Sync',
        'documento', '12345678000199',
        'email', 'cliente.piloto@example.com',
        'telefone', '11999999999',
        'ativo', true
    )::text AS payload_json,
    'fixture-cliente-piloto-001'::text AS trace_id;
