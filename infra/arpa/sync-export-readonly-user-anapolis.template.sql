-- Template para criar usuario runtime read-only do SyncAgent no Arpa Anapolis.
-- Revisar a senha e executar como DBA/admin. Nao usar o usuario postgres no SyncAgent.

-- 1. Criar usuario runtime sem permissoes administrativas.
CREATE ROLE sync_agent_anapolis_ro
    LOGIN
    PASSWORD '<definir-senha-forte>'
    NOSUPERUSER
    NOCREATEDB
    NOCREATEROLE
    NOINHERIT
    NOREPLICATION;

-- 2. Permitir conexao ao banco.
GRANT CONNECT ON DATABASE anapolis TO sync_agent_anapolis_ro;

-- 3. Permitir leitura apenas nas views de exportacao.
GRANT USAGE ON SCHEMA sync_export TO sync_agent_anapolis_ro;
GRANT SELECT ON sync_export.produtos TO sync_agent_anapolis_ro;
GRANT SELECT ON sync_export.clientes TO sync_agent_anapolis_ro;

-- 4. Garantir ausencia de privilegios de escrita/DDL no schema de exportacao.
REVOKE CREATE ON SCHEMA sync_export FROM sync_agent_anapolis_ro;
REVOKE INSERT, UPDATE, DELETE, TRUNCATE, REFERENCES, TRIGGER
    ON sync_export.produtos FROM sync_agent_anapolis_ro;
REVOKE INSERT, UPDATE, DELETE, TRUNCATE, REFERENCES, TRIGGER
    ON sync_export.clientes FROM sync_agent_anapolis_ro;

-- 5. Reduzir risco de uso acidental em tabelas public.
REVOKE CREATE ON SCHEMA public FROM sync_agent_anapolis_ro;
REVOKE ALL PRIVILEGES ON ALL TABLES IN SCHEMA public FROM sync_agent_anapolis_ro;

