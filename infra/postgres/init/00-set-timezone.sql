-- Define o timezone padrao (banco atual + role dona do banco atual), sem
-- depender de nenhum nome fixo de banco/usuario - descobre os dois em
-- runtime via current_database() e o dono (datdba) do banco atual. Antes
-- tinha "pdv"/"araras" hardcoded, que ja nao batia nem com o default atual
-- do instalador Windows (ararasuite) nem com o docker-compose de dev
-- (pdv_sync) - qualquer troca de nome de banco/usuario quebrava isso
-- silenciosamente ("role ... nao existe" no bootstrap).
DO $$
DECLARE
    owner_role text;
BEGIN
    SELECT pg_catalog.pg_get_userbyid(datdba) INTO owner_role
    FROM pg_catalog.pg_database
    WHERE datname = current_database();

    EXECUTE format('ALTER DATABASE %I SET timezone TO %L', current_database(), 'America/Sao_Paulo');
    EXECUTE format('ALTER ROLE %I SET timezone TO %L', owner_role, 'America/Sao_Paulo');
END
$$;
