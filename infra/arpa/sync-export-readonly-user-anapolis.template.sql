-- Template para criar usuario runtime read-only do SyncAgent no Arpa Anapolis.
-- Revisar a senha e executar como DBA/admin. Nao usar o usuario postgres no SyncAgent.
--
-- NOTA (1.6.9+): o botao "Preparar views" (Configuracoes > Arpa) ja concede
-- USAGE + SELECT em todo o schema sync_export ao Usuario da conexao. Este
-- template so e necessario para CRIAR o role (senha) - os GRANTs abaixo sao
-- redundantes com o que o botao faz, e cobrem so produtos/clientes (falta
-- estoque). Preferir: criar o role aqui (ou pelo botao "Criar usuario
-- read-only") e deixar o "Preparar views" cuidar dos grants.

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
GRANT SELECT ON ALL TABLES IN SCHEMA sync_export TO sync_agent_anapolis_ro;
ALTER DEFAULT PRIVILEGES IN SCHEMA sync_export GRANT SELECT ON TABLES TO sync_agent_anapolis_ro;

-- 4. Garantir ausencia de privilegios de escrita/DDL no schema de exportacao.
REVOKE CREATE ON SCHEMA sync_export FROM sync_agent_anapolis_ro;
REVOKE INSERT, UPDATE, DELETE, TRUNCATE, REFERENCES, TRIGGER
    ON ALL TABLES IN SCHEMA sync_export FROM sync_agent_anapolis_ro;

-- 5. Reduzir risco de uso acidental em tabelas public.
REVOKE CREATE ON SCHEMA public FROM sync_agent_anapolis_ro;
REVOKE ALL PRIVILEGES ON ALL TABLES IN SCHEMA public FROM sync_agent_anapolis_ro;

