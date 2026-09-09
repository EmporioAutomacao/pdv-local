-- =====================================================================
-- sync_export: views read-only lidas pelo SyncAgent no banco Arpa.
--
-- ARQUIVO UNICO E GENERICO. Nao editar por cliente. E executado uma vez
-- por conexao pelo botao "Preparar views" (Configuracoes > Arpa) com uma
-- credencial DBA transitoria informada na hora e nunca gravada.
--
-- O bloco DO abaixo introspecta o schema real do Arpa (nomes de tabela e
-- coluna variam entre versoes) e (re)cria:
--   sync_export.produtos   -> cadastro + preco (precocusto/precovenda)
--   sync_export.clientes
--   sync_export.estoque    -> so se houver coluna de quantidade em produtos
--   sync_export.vendas / .financeiro -> best-effort, so se as tabelas padrao existirem
--
-- occurred_at_utc (o SyncAgent so re-le linhas com occurred_at_utc >
-- watermark) vem, em ordem de preferencia:
--   1. tabela de log de alteracao companheira (alterados / alterados_clientes /
--      produtos_altera_quantidade) -> sincronizacao incremental de verdade;
--   2. coluna temporal na propria tabela (updated_at / data_alteracao / ...);
--   3. timestamp fixo 2000-01-01 (carga inicial: sincroniza uma vez e para).
--
-- Timestamps sem timezone sao interpretados no fuso do servidor quando ele e
-- um offset fixo; senao no horario de Brasilia fixo (Etc/GMT+3, sem DST) e
-- convertidos para instante UTC.
--
-- Contrato de cada view: entity_key text, occurred_at_utc timestamptz,
-- payload_json text, trace_id text.
-- =====================================================================

CREATE SCHEMA IF NOT EXISTS sync_export;

-- Helpers de introspecao (ficam no schema; o usuario runtime nao recebe
-- GRANT neles, so nas views).
CREATE OR REPLACE FUNCTION sync_export._pick(p_table text, p_candidates text[])
RETURNS text LANGUAGE sql STABLE AS $fn$
    SELECT c.column_name::text
    FROM information_schema.columns c
    WHERE c.table_schema = 'public' AND c.table_name::text = p_table
      AND c.column_name::text = ANY (p_candidates)
    ORDER BY array_position(p_candidates, c.column_name::text)
    LIMIT 1
$fn$;

CREATE OR REPLACE FUNCTION sync_export._first_table(p_candidates text[])
RETURNS text LANGUAGE sql STABLE AS $fn$
    SELECT t.table_name::text
    FROM information_schema.tables t
    WHERE t.table_schema = 'public' AND t.table_name::text = ANY (p_candidates)
    ORDER BY array_position(p_candidates, t.table_name::text)
    LIMIT 1
$fn$;

CREATE OR REPLACE FUNCTION sync_export._coltype(p_table text, p_col text)
RETURNS text LANGUAGE sql STABLE AS $fn$
    SELECT c.data_type::text
    FROM information_schema.columns c
    WHERE c.table_schema = 'public' AND c.table_name::text = p_table AND c.column_name::text = p_col
    LIMIT 1
$fn$;

-- occurred_at_utc a partir de uma coluna temporal, respeitando o tipo dela.
CREATE OR REPLACE FUNCTION sync_export._occurred_expr(p_alias text, p_table text, p_col text, p_tz text)
RETURNS text LANGUAGE plpgsql STABLE AS $fn$
DECLARE t text;
BEGIN
    t := sync_export._coltype(p_table, p_col);
    IF t = 'timestamp with time zone' THEN
        RETURN format('%s.%I', p_alias, p_col);
    ELSIF t = 'date' THEN
        RETURN format('(%s.%I::timestamp AT TIME ZONE %L)', p_alias, p_col, p_tz);
    ELSE  -- timestamp without time zone (ou desconhecido)
        RETURN format('(%s.%I AT TIME ZONE %L)', p_alias, p_col, p_tz);
    END IF;
END;
$fn$;

DO $sync_export$
DECLARE
    v_tz        text := 'America/Sao_Paulo';
    fixed       text := 'TIMESTAMPTZ ''2000-01-01 00:00:00+00''';

    -- produto
    p_t text; p_code text; p_name text; p_bc text; p_fab text; p_ncm text;
    p_cost text; p_sale text; p_active text;
    p_qty text; p_min text; p_max text; p_loc text; p_updated text;
    p_log text; p_log_key text; p_log_time text;
    p_qlog text; p_qlog_key text; p_qlog_time text;

    -- cliente
    c_t text; c_code text; c_name text; c_doc text; c_email text; c_phone text; c_active text; c_updated text;
    c_log text; c_log_key text; c_log_time text;

    occ text; frm text; active_expr text; pairs text; sql text; src text;
BEGIN
    -- ---- fuso para interpretar os timestamps naive do Arpa ----
    -- Default: horario de Brasilia FIXO (Etc/GMT+3 = UTC-3, sem DST). Nomes de
    -- regiao IANA (ex.: America/Sao_Paulo) em PostgreSQL antigo (piloto e 9.6,
    -- tzdata pre-2019) aplicam DST fantasma no verao e erram 1h. So aproveitamos
    -- o TimeZone do servidor quando ele ja e um offset fixo (sem '/').
    BEGIN
        v_tz := 'Etc/GMT+3';
        IF current_setting('TimeZone') IS NOT NULL
           AND position('/' in current_setting('TimeZone')) = 0
           AND upper(current_setting('TimeZone')) NOT IN ('GMT', 'UTC', 'UCT', 'ZULU', 'GREENWICH', 'LOCALTIME', 'FACTORY') THEN
            v_tz := current_setting('TimeZone');
        END IF;
    EXCEPTION WHEN OTHERS THEN
        v_tz := 'Etc/GMT+3';
    END;

    -- =================== PRODUTOS + ESTOQUE ===================
  BEGIN
    p_t := sync_export._first_table(ARRAY['produtos', 'produto']);
    IF p_t IS NULL THEN
        RAISE NOTICE 'sync_export: tabela de produtos nao encontrada; produtos e estoque pulados.';
    ELSE
        p_code   := sync_export._pick(p_t, ARRAY['codigo', 'cod_produto', 'id']);
        p_name   := sync_export._pick(p_t, ARRAY['descricao', 'nome', 'descricao_produto']);
        p_bc     := sync_export._pick(p_t, ARRAY['codigodebarras', 'codigo_barras', 'codigobarras', 'codbarra', 'ean', 'gtin', 'ean13', 'barcode']);
        p_fab    := sync_export._pick(p_t, ARRAY['codigodefabrica', 'codigo_fabrica', 'cod_fabrica', 'referencia']);
        p_ncm    := sync_export._pick(p_t, ARRAY['cod_ncm', 'ncm', 'codigo_ncm']);
        p_cost   := sync_export._pick(p_t, ARRAY['precocusto', 'preco_custo', 'custo', 'valorcusto', 'vlr_custo']);
        p_sale   := sync_export._pick(p_t, ARRAY['precovenda', 'preco_venda', 'preco', 'valorvenda', 'vlr_venda']);
        p_active := sync_export._pick(p_t, ARRAY['ativo', 'status', 'situacao']);
        p_qty    := sync_export._pick(p_t, ARRAY['quantidade', 'qtd', 'estoque', 'saldo', 'qtde']);
        p_min    := sync_export._pick(p_t, ARRAY['estoqueminimo', 'estoque_minimo', 'minimo', 'qtd_minima']);
        p_max    := sync_export._pick(p_t, ARRAY['estoquemaximo', 'estoque_maximo', 'maximo', 'qtd_maxima']);
        p_loc    := sync_export._pick(p_t, ARRAY['localizacao', 'local', 'endereco_estoque', 'prateleira']);
        p_updated := sync_export._pick(p_t, ARRAY['updated_at_utc', 'atualizado_em', 'updated_at', 'data_alteracao', 'dt_alteracao', 'ultima_alteracao', 'data_atualizacao']);

        p_log := sync_export._first_table(ARRAY['alterados', 'produtos_alterados', 'produto_alterados', 'log_produtos', 'produtos_log_alteracao']);
        IF p_log IS NOT NULL THEN
            p_log_key  := sync_export._pick(p_log, ARRAY['produto', 'codigo', 'cod_produto', 'id_produto']);
            p_log_time := sync_export._pick(p_log, ARRAY['data', 'datahora', 'data_alteracao', 'updated_at', 'data_hora']);
            IF p_log_key IS NULL OR p_log_time IS NULL THEN p_log := NULL; END IF;
        END IF;

        p_qlog := sync_export._first_table(ARRAY['produtos_altera_quantidade', 'produto_altera_quantidade', 'log_estoque', 'movimento_estoque_log']);
        IF p_qlog IS NOT NULL THEN
            p_qlog_key  := sync_export._pick(p_qlog, ARRAY['produto', 'codigo', 'cod_produto', 'id_produto']);
            p_qlog_time := sync_export._pick(p_qlog, ARRAY['data', 'datahora', 'data_movimento', 'data_alteracao']);
            IF p_qlog_key IS NULL OR p_qlog_time IS NULL THEN p_qlog := NULL; END IF;
        END IF;

        IF p_code IS NULL OR p_name IS NULL THEN
            RAISE NOTICE 'sync_export: produtos sem codigo/descricao detectaveis; produtos e estoque pulados.';
        ELSE
            -- ativo: Arpa Sistemas legado usa numerico invertido (0/A = ativo)
            IF p_active IS NULL THEN
                active_expr := 'true';
            ELSIF p_active IN ('status', 'situacao') THEN
                active_expr := format(
                    $x$CASE WHEN upper(cast(p.%1$I AS text)) IN ('A','ATIVO','1','TRUE','T','SIM','S') THEN true
                            WHEN upper(cast(p.%1$I AS text)) IN ('I','INATIVO','0','FALSE','F','NAO','N') THEN false
                            ELSE true END$x$, p_active);
            ELSE
                active_expr := format(
                    $x$CASE WHEN upper(cast(p.%1$I AS text)) IN ('0','FALSE','F','A','ATIVO') THEN true
                            WHEN upper(cast(p.%1$I AS text)) IN ('1','TRUE','T','I','INATIVO') THEN false
                            ELSE true END$x$, p_active);
            END IF;

            -- occurred_at + FROM
            IF p_log IS NOT NULL THEN
                frm := format(' FROM public.%I p JOIN public.%I chg ON chg.%I = p.%I', p_t, p_log, p_log_key, p_code);
                occ := format('COALESCE(%s, %s)', sync_export._occurred_expr('chg', p_log, p_log_time, v_tz), fixed);
                src := 'log ' || p_log;
            ELSIF p_updated IS NOT NULL THEN
                frm := format(' FROM public.%I p', p_t);
                occ := format('COALESCE(%s, %s)', sync_export._occurred_expr('p', p_t, p_updated, v_tz), fixed);
                src := 'coluna ' || p_updated;
            ELSE
                frm := format(' FROM public.%I p', p_t);
                occ := fixed;
                src := 'timestamp fixo (carga inicial)';
            END IF;

            pairs := format('''codigo'', p.%I, ''descricao'', p.%I', p_code, p_name);
            IF p_fab  IS NOT NULL THEN pairs := pairs || format(', ''codigodefabrica'', p.%I', p_fab); END IF;
            IF p_ncm  IS NOT NULL THEN pairs := pairs || format(', ''cod_ncm'', p.%I', p_ncm); END IF;
            IF p_bc   IS NOT NULL THEN pairs := pairs || format(', ''codigodebarras'', p.%I', p_bc); END IF;
            IF p_cost IS NOT NULL THEN pairs := pairs || format(', ''precocusto'', p.%I', p_cost); END IF;
            IF p_sale IS NOT NULL THEN pairs := pairs || format(', ''precovenda'', p.%I', p_sale); END IF;
            pairs := pairs || ', ''ativo'', ' || active_expr;

            sql := 'CREATE OR REPLACE VIEW sync_export.produtos AS SELECT '
                || format('p.%I::text', p_code) || ' AS entity_key, '
                || occ || ' AS occurred_at_utc, '
                || 'jsonb_build_object(' || pairs || ')::text AS payload_json, '
                || format('(''arpa-produto-'' || p.%I)::text', p_code) || ' AS trace_id'
                || frm
                || format(' WHERE p.%I IS NOT NULL', p_code);
            EXECUTE sql;
            RAISE NOTICE 'sync_export.produtos criada (occurred_at via %).', src;

            -- =================== ESTOQUE ===================
            IF p_qty IS NULL THEN
                RAISE NOTICE 'sync_export: sem coluna de quantidade em produtos; estoque pulado.';
            ELSE
                IF p_qlog IS NOT NULL THEN
                    -- max() de um timestamp sem tz continua sem tz -> converte no fuso do servidor
                    frm := format(
                        ' FROM public.%I p JOIN (SELECT %I AS k, max(%I) AS d FROM public.%I GROUP BY %I) q ON q.k = p.%I',
                        p_t, p_qlog_key, p_qlog_time, p_qlog, p_qlog_key, p_code);
                    occ := format('COALESCE((q.d AT TIME ZONE %L), %s)', v_tz, fixed);
                    src := 'log ' || p_qlog;
                ELSIF p_log IS NOT NULL THEN
                    frm := format(' FROM public.%I p JOIN public.%I chg ON chg.%I = p.%I', p_t, p_log, p_log_key, p_code);
                    occ := format('COALESCE(%s, %s)', sync_export._occurred_expr('chg', p_log, p_log_time, v_tz), fixed);
                    src := 'log ' || p_log;
                ELSE
                    frm := format(' FROM public.%I p', p_t);
                    occ := fixed;
                    src := 'timestamp fixo (carga inicial)';
                END IF;

                pairs := format('''codigo'', p.%I, ''quantidade'', p.%I', p_code, p_qty);
                IF p_min IS NOT NULL THEN pairs := pairs || format(', ''estoqueminimo'', p.%I', p_min); END IF;
                IF p_max IS NOT NULL THEN pairs := pairs || format(', ''estoquemaximo'', p.%I', p_max); END IF;
                IF p_loc IS NOT NULL THEN pairs := pairs || format(', ''localizacao'', p.%I', p_loc); END IF;

                sql := 'CREATE OR REPLACE VIEW sync_export.estoque AS SELECT '
                    || format('p.%I::text', p_code) || ' AS entity_key, '
                    || occ || ' AS occurred_at_utc, '
                    || 'jsonb_build_object(' || pairs || ')::text AS payload_json, '
                    || format('(''arpa-estoque-'' || p.%I)::text', p_code) || ' AS trace_id'
                    || frm
                    || format(' WHERE p.%I IS NOT NULL', p_code);
                EXECUTE sql;
                RAISE NOTICE 'sync_export.estoque criada (occurred_at via %).', src;
            END IF;
        END IF;
    END IF;
  EXCEPTION WHEN OTHERS THEN
    RAISE NOTICE 'sync_export: produtos/estoque nao criados (%).', SQLERRM;
  END;

    -- =================== CLIENTES ===================
  BEGIN
    c_t := sync_export._first_table(ARRAY['clientes', 'cliente']);
    IF c_t IS NULL THEN
        RAISE NOTICE 'sync_export: tabela de clientes nao encontrada; clientes pulado.';
    ELSE
        c_code   := sync_export._pick(c_t, ARRAY['codigo', 'id', 'cod_cliente']);
        c_name   := sync_export._pick(c_t, ARRAY['nome', 'razao_social', 'razao', 'fantasia']);
        c_doc    := sync_export._pick(c_t, ARRAY['cpf_cnpj', 'cnpj_cpf', 'cnpjcpf', 'cnpj', 'cpf', 'documento', 'doc']);
        c_email  := sync_export._pick(c_t, ARRAY['email', 'e_mail']);
        c_phone  := sync_export._pick(c_t, ARRAY['fone', 'telefone', 'celular', 'fone1', 'telefone1']);
        c_active := sync_export._pick(c_t, ARRAY['status', 'ativo', 'situacao']);
        c_updated := sync_export._pick(c_t, ARRAY['updated_at_utc', 'atualizado_em', 'updated_at', 'data_alteracao', 'dt_alteracao', 'ultima_alteracao']);

        c_log := sync_export._first_table(ARRAY['alterados_clientes', 'clientes_alterados', 'cliente_alterados', 'cliente_log_alteracoes', 'log_clientes']);
        IF c_log IS NOT NULL THEN
            c_log_key  := sync_export._pick(c_log, ARRAY['cliente', 'codigo', 'cod_cliente', 'id_cliente']);
            c_log_time := sync_export._pick(c_log, ARRAY['data', 'datahora', 'data_alteracao', 'updated_at', 'data_hora']);
            IF c_log_key IS NULL OR c_log_time IS NULL THEN c_log := NULL; END IF;
        END IF;

        IF c_code IS NULL OR c_name IS NULL THEN
            RAISE NOTICE 'sync_export: clientes sem codigo/nome detectaveis; clientes pulado.';
        ELSE
            IF c_active IS NULL THEN
                active_expr := 'true';
            ELSE
                active_expr := format(
                    $x$CASE WHEN upper(cast(c.%1$I AS text)) IN ('A','ATIVO','1','TRUE','T','SIM','S') THEN true
                            WHEN upper(cast(c.%1$I AS text)) IN ('I','INATIVO','0','FALSE','F','NAO','N') THEN false
                            ELSE true END$x$, c_active);
            END IF;

            IF c_log IS NOT NULL THEN
                frm := format(' FROM public.%I c JOIN public.%I chg ON chg.%I = c.%I', c_t, c_log, c_log_key, c_code);
                occ := format('COALESCE(%s, %s)', sync_export._occurred_expr('chg', c_log, c_log_time, v_tz), fixed);
                src := 'log ' || c_log;
            ELSIF c_updated IS NOT NULL THEN
                frm := format(' FROM public.%I c', c_t);
                occ := format('COALESCE(%s, %s)', sync_export._occurred_expr('c', c_t, c_updated, v_tz), fixed);
                src := 'coluna ' || c_updated;
            ELSE
                frm := format(' FROM public.%I c', c_t);
                occ := fixed;
                src := 'timestamp fixo (carga inicial)';
            END IF;

            pairs := format('''codigo'', c.%I, ''nome'', c.%I', c_code, c_name);
            IF c_doc   IS NOT NULL THEN pairs := pairs || format(', ''documento'', c.%I', c_doc); END IF;
            IF c_email IS NOT NULL THEN pairs := pairs || format(', ''email'', c.%I', c_email); END IF;
            IF c_phone IS NOT NULL THEN pairs := pairs || format(', ''telefone'', c.%I', c_phone); END IF;
            pairs := pairs || ', ''ativo'', ' || active_expr;

            sql := 'CREATE OR REPLACE VIEW sync_export.clientes AS SELECT '
                || format('c.%I::text', c_code) || ' AS entity_key, '
                || occ || ' AS occurred_at_utc, '
                || 'jsonb_build_object(' || pairs || ')::text AS payload_json, '
                || format('(''arpa-cliente-'' || c.%I)::text', c_code) || ' AS trace_id'
                || frm
                || format(' WHERE c.%I IS NOT NULL', c_code);
            EXECUTE sql;
            RAISE NOTICE 'sync_export.clientes criada (occurred_at via %).', src;
        END IF;
    END IF;
  EXCEPTION WHEN OTHERS THEN
    RAISE NOTICE 'sync_export: clientes nao criado (%).', SQLERRM;
  END;
END;
$sync_export$;

-- =================== VENDAS / FINANCEIRO (best-effort) ===================
-- So criadas se o schema padrao Arpa (pedidos/itenspedido/contas_receber)
-- existir. Caso contrario ficam ausentes e os toggles Vendas/Financeiro da
-- conexao devem ficar desmarcados.
DO $vf$
BEGIN
    BEGIN
        EXECUTE $v$
            CREATE OR REPLACE VIEW sync_export.vendas AS
            SELECT v.codigo::text AS entity_key,
                COALESCE(v.updated_at_utc, v.data, now())::timestamptz AS occurred_at_utc,
                jsonb_build_object(
                    'codigo_venda_arpa', v.codigo, 'data', COALESCE(v.data, v.updated_at_utc),
                    'status', v.status, 'vendedor_codigo', v.codvendedor,
                    'desconto_total', v.desconto, 'especie_pagamento', v.forma_pagamento,
                    'cliente_documento', c.cnpj_cpf, 'cliente_nome', c.nome,
                    'itens', COALESCE((SELECT jsonb_agg(jsonb_build_object(
                        'codigo_produto_arpa', i.codproduto, 'quantidade', i.quantidade,
                        'valor_unitario', i.valorunitario, 'desconto', i.desconto))
                        FROM public.itenspedido i WHERE i.codpedido = v.codigo), '[]'::jsonb)
                )::text AS payload_json,
                concat('arpa-venda-', v.codigo)::text AS trace_id
            FROM public.pedidos v
            LEFT JOIN public.clientes c ON c.codigo = v.codcliente
            WHERE v.codigo IS NOT NULL
        $v$;
        RAISE NOTICE 'sync_export.vendas criada.';
    EXCEPTION WHEN OTHERS THEN
        RAISE NOTICE 'sync_export.vendas pulada (%).', SQLERRM;
    END;

    BEGIN
        EXECUTE $f$
            CREATE OR REPLACE VIEW sync_export.financeiro AS
            SELECT r.codigo::text AS entity_key,
                COALESCE(r.updated_at_utc, r.datapagamento, r.vencimento, now())::timestamptz AS occurred_at_utc,
                jsonb_build_object(
                    'titulo_externo_id', r.codigo, 'natureza', 'receber',
                    'codigo_venda_arpa', r.codpedido, 'cliente_documento', c.cnpj_cpf,
                    'cliente_nome', c.nome, 'especie_pagamento', r.forma_pagamento,
                    'valor_base', r.valor, 'valor_recebido', r.valorpago,
                    'vencimento', r.vencimento, 'data_recebimento', r.datapagamento,
                    'status', r.status, 'documento', r.documento
                )::text AS payload_json,
                concat('arpa-financeiro-', r.codigo)::text AS trace_id
            FROM public.contas_receber r
            LEFT JOIN public.clientes c ON c.codigo = r.codcliente
            WHERE r.codigo IS NOT NULL
        $f$;
        RAISE NOTICE 'sync_export.financeiro criada.';
    EXCEPTION WHEN OTHERS THEN
        RAISE NOTICE 'sync_export.financeiro pulada (%).', SQLERRM;
    END;
END;
$vf$;
