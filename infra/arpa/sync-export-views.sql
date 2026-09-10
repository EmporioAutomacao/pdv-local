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
DECLARE
    fin_receber_sql text;
    fin_pagar_sql   text := '';
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

    -- financeiro: contas_receber (natureza=receber) + contas_pagar
    -- (natureza=pagar, so se a tabela existir). O ERP grava TituloReceber /
    -- TituloPagar conforme `natureza`. entity_key do pagar leva prefixo 'pag-'
    -- para nao colidir com o codigo de um titulo a receber homonimo.
    fin_receber_sql := $f$
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

    IF to_regclass('public.contas_pagar') IS NOT NULL THEN
        fin_pagar_sql := $fp$
            UNION ALL
            SELECT ('pag-' || p.codigo)::text AS entity_key,
                COALESCE(p.updated_at_utc, p.datapagamento, p.vencimento, now())::timestamptz AS occurred_at_utc,
                jsonb_build_object(
                    'titulo_externo_id', p.codigo, 'natureza', 'pagar',
                    'compra_externa_id', p.codpedido, 'documento', p.documento,
                    'fornecedor_codigo', p.codfornecedor, 'fornecedor_nome', f.nome,
                    'especie_pagamento', p.forma_pagamento,
                    'valor_base', p.valor, 'valor_pago', p.valorpago,
                    'vencimento', p.vencimento, 'data_pagamento', p.datapagamento,
                    'emissao', p.dataemissao, 'status', p.status
                )::text AS payload_json,
                concat('arpa-financeiro-pag-', p.codigo)::text AS trace_id
            FROM public.contas_pagar p
            LEFT JOIN public.clientes f ON f.codigo = p.codfornecedor
            WHERE p.codigo IS NOT NULL
        $fp$;
    END IF;

    BEGIN
        EXECUTE 'CREATE OR REPLACE VIEW sync_export.financeiro AS ' || fin_receber_sql || fin_pagar_sql;
        RAISE NOTICE 'sync_export.financeiro criada%.',
            CASE WHEN fin_pagar_sql <> '' THEN ' (receber + pagar)' ELSE ' (receber)' END;
    EXCEPTION WHEN OTHERS THEN
        -- o ramo de contas_pagar bateu num schema diferente do template;
        -- recria so com contas_receber para nao regredir o que ja funcionava.
        BEGIN
            EXECUTE 'CREATE OR REPLACE VIEW sync_export.financeiro AS ' || fin_receber_sql;
            RAISE NOTICE 'sync_export.financeiro criada (receber; pagar pulado: %).', SQLERRM;
        EXCEPTION WHEN OTHERS THEN
            RAISE NOTICE 'sync_export.financeiro pulada (%).', SQLERRM;
        END;
    END;
END;
$vf$;

-- =================== COBRANCA / PLANO HISTORICO (best-effort) ===================
-- entity_type=cobranca (contrato Sync 2.10.0) e plano_historico (2.11.0). Payload
-- em chaves cruas; o ERP normaliza (infere banco, limpa digitos, monta cedente /
-- recalcula codigo_erp). So criadas se as tabelas padrao existirem.
DO $cob$
DECLARE
    v_tz  text := 'Etc/GMT+3';
    fixed text := 'TIMESTAMPTZ ''2000-01-01 00:00:00+00''';
    t text; pairs text; sql text;
    c_ext text; c_nome text; c_bcod text; c_bnome text; c_ag text; c_agdv text;
    c_cta text; c_ctadv text; c_cart text; c_varc text; c_conv text; c_benef text;
    c_posto text; c_coop text; c_sigla text; c_instr text;
    c_cednome text; c_ceddoc text; c_cedmail text; c_cedtel text; c_cedcid text; c_ceduf text;
    c_ativo text; c_padr text; c_upd text;
    p_code text; p_ana text; p_desc text; p_ta text; p_es text; p_padr text;
    p_calc text; p_tipo text; p_oper text; p_ativo text;
BEGIN
    BEGIN
        IF current_setting('TimeZone') IS NOT NULL
           AND position('/' in current_setting('TimeZone')) = 0
           AND upper(current_setting('TimeZone')) NOT IN ('GMT','UTC','UCT','ZULU','GREENWICH','LOCALTIME','FACTORY') THEN
            v_tz := current_setting('TimeZone');
        END IF;
    EXCEPTION WHEN OTHERS THEN v_tz := 'Etc/GMT+3';
    END;

  BEGIN
    t := sync_export._first_table(ARRAY['ctabancarias','contas_bancarias','conta_bancaria','contabancaria',
        'contas_cobranca','conta_cobranca','cadastro_bancos','bancos_conta','contasbancarias']);
    IF t IS NULL THEN
        RAISE NOTICE 'sync_export.cobranca pulada (tabela de contas bancarias nao encontrada).';
    ELSE
        c_ext    := sync_export._pick(t, ARRAY['id','codigo','cod_conta','conta_id','id_conta','registro_id']);
        c_nome   := sync_export._pick(t, ARRAY['nome_exibicao','descricao','nome','conta_nome','descricao_conta','apelido']);
        c_bcod   := sync_export._pick(t, ARRAY['banco_codigo','codigo_banco','cod_banco','banco_numero','banco']);
        c_bnome  := sync_export._pick(t, ARRAY['banco_nome','nome_banco','descricao_banco']);
        c_ag     := sync_export._pick(t, ARRAY['agencia','ag','codigo_agencia','agencia_numero']);
        c_agdv   := sync_export._pick(t, ARRAY['agencia_digito','digito_agencia','dv_agencia','agencia_dv']);
        c_cta    := sync_export._pick(t, ARRAY['conta','numero_conta','num_conta','conta_numero']);
        c_ctadv  := sync_export._pick(t, ARRAY['conta_digito','digito_conta','dv_conta','conta_dv']);
        c_cart   := sync_export._pick(t, ARRAY['carteira','codigo_carteira','cod_carteira']);
        c_varc   := sync_export._pick(t, ARRAY['variacao_carteira','variacao','carteira_variacao']);
        c_conv   := sync_export._pick(t, ARRAY['convenio','codigo_convenio','cod_convenio']);
        c_benef  := sync_export._pick(t, ARRAY['codigo_beneficiario','cod_beneficiario','codigo_transmissao','beneficiario_codigo']);
        c_posto  := sync_export._pick(t, ARRAY['posto','codigo_posto']);
        c_coop   := sync_export._pick(t, ARRAY['cooperativa','codigo_cooperativa']);
        c_sigla  := sync_export._pick(t, ARRAY['sigla','apelido_banco','nome_curto']);
        c_instr  := sync_export._pick(t, ARRAY['instrucao_padrao','instrucao_boleto','instrucoes','mensagem_boleto']);
        c_cednome:= sync_export._pick(t, ARRAY['cedente_nome','nome_cedente','cedente','beneficiario','razao_social']);
        c_ceddoc := sync_export._pick(t, ARRAY['cedente_documento','documento_cedente','cnpj','cnpj_cpf','cpf_cnpj','documento']);
        c_cedmail:= sync_export._pick(t, ARRAY['cedente_email','email_cedente','email']);
        c_cedtel := sync_export._pick(t, ARRAY['cedente_telefone','telefone_cedente','telefone','fone']);
        c_cedcid := sync_export._pick(t, ARRAY['cedente_cidade','cidade_cedente','cidade']);
        c_ceduf  := sync_export._pick(t, ARRAY['cedente_uf','uf_cedente','uf','estado']);
        c_ativo  := sync_export._pick(t, ARRAY['ativo','habilitado','status','enabled']);
        c_padr   := sync_export._pick(t, ARRAY['padrao','default','is_default','principal']);
        c_upd    := sync_export._pick(t, ARRAY['updated_at_utc','atualizado_em','updated_at','data_alteracao']);

        IF c_ext IS NULL OR c_ag IS NULL OR c_cta IS NULL OR (c_bcod IS NULL AND c_bnome IS NULL) THEN
            RAISE NOTICE 'sync_export.cobranca pulada (tabela % sem id/agencia/conta/banco).', t;
        ELSE
            pairs := format('''externo_id'', b.%I, ''agencia'', b.%I, ''conta'', b.%I', c_ext, c_ag, c_cta);
            IF c_nome    IS NOT NULL THEN pairs := pairs || format(', ''nome_exibicao'', b.%I', c_nome); END IF;
            IF c_bcod    IS NOT NULL THEN pairs := pairs || format(', ''banco_codigo'', b.%I', c_bcod); END IF;
            IF c_bnome   IS NOT NULL THEN pairs := pairs || format(', ''banco_nome'', b.%I', c_bnome); END IF;
            IF c_agdv    IS NOT NULL THEN pairs := pairs || format(', ''agencia_digito'', b.%I', c_agdv); END IF;
            IF c_ctadv   IS NOT NULL THEN pairs := pairs || format(', ''conta_digito'', b.%I', c_ctadv); END IF;
            IF c_cart    IS NOT NULL THEN pairs := pairs || format(', ''carteira'', b.%I', c_cart); END IF;
            IF c_varc    IS NOT NULL THEN pairs := pairs || format(', ''variacao_carteira'', b.%I', c_varc); END IF;
            IF c_conv    IS NOT NULL THEN pairs := pairs || format(', ''convenio'', b.%I', c_conv); END IF;
            IF c_benef   IS NOT NULL THEN pairs := pairs || format(', ''codigo_beneficiario'', b.%I', c_benef); END IF;
            IF c_posto   IS NOT NULL THEN pairs := pairs || format(', ''posto'', b.%I', c_posto); END IF;
            IF c_coop    IS NOT NULL THEN pairs := pairs || format(', ''cooperativa'', b.%I', c_coop); END IF;
            IF c_sigla   IS NOT NULL THEN pairs := pairs || format(', ''sigla'', b.%I', c_sigla); END IF;
            IF c_instr   IS NOT NULL THEN pairs := pairs || format(', ''instrucao_padrao'', b.%I', c_instr); END IF;
            IF c_cednome IS NOT NULL THEN pairs := pairs || format(', ''cedente_nome'', b.%I', c_cednome); END IF;
            IF c_ceddoc  IS NOT NULL THEN pairs := pairs || format(', ''cedente_documento'', b.%I', c_ceddoc); END IF;
            IF c_cedmail IS NOT NULL THEN pairs := pairs || format(', ''cedente_email'', b.%I', c_cedmail); END IF;
            IF c_cedtel  IS NOT NULL THEN pairs := pairs || format(', ''cedente_telefone'', b.%I', c_cedtel); END IF;
            IF c_cedcid  IS NOT NULL THEN pairs := pairs || format(', ''cedente_cidade'', b.%I', c_cedcid); END IF;
            IF c_ceduf   IS NOT NULL THEN pairs := pairs || format(', ''cedente_uf'', b.%I', c_ceduf); END IF;
            IF c_ativo   IS NOT NULL THEN pairs := pairs || format(', ''ativo_raw'', b.%I', c_ativo); END IF;
            IF c_padr    IS NOT NULL THEN pairs := pairs || format(', ''padrao_raw'', b.%I', c_padr); END IF;

            sql := 'CREATE OR REPLACE VIEW sync_export.cobranca AS SELECT '
                || format('b.%I::text', c_ext) || ' AS entity_key, '
                || COALESCE(CASE WHEN c_upd IS NOT NULL
                       THEN 'COALESCE(' || sync_export._occurred_expr('b', t, c_upd, v_tz) || ', ' || fixed || ')' END, fixed)
                || ' AS occurred_at_utc, jsonb_build_object(' || pairs || ')::text AS payload_json, '
                || format('(''arpa-cobranca-'' || b.%I)::text', c_ext) || ' AS trace_id '
                || format('FROM public.%I b WHERE b.%I IS NOT NULL', t, c_ext);
            EXECUTE sql;
            RAISE NOTICE 'sync_export.cobranca criada (tabela %).', t;
        END IF;
    END IF;
  EXCEPTION WHEN OTHERS THEN
    RAISE NOTICE 'sync_export.cobranca pulada (%).', SQLERRM;
  END;

  BEGIN
    t := sync_export._first_table(ARRAY['plano_historicos','plano_historico','planohistoricos','planohistorico',
        'historicos','historico','historicos_financeiro','historico_financeiro']);
    IF t IS NULL THEN
        RAISE NOTICE 'sync_export.plano_historico pulada (tabela de historicos nao encontrada).';
    ELSE
        p_code  := sync_export._pick(t, ARRAY['codigo','cod_historico','id','historico']);
        p_desc  := sync_export._pick(t, ARRAY['descricao','nome','historico_descricao','descricao_historico']);
        p_ana   := sync_export._pick(t, ARRAY['analitico','nivel','conta_analitica','contabil','mascara','plano']);
        p_ta    := sync_export._pick(t, ARRAY['tipo_ta','t_a','ta','tipo_analitico','tipoa','ta_tipo']);
        p_es    := sync_export._pick(t, ARRAY['es','e_s','entrada_saida','tipo_es']);
        p_padr  := sync_export._pick(t, ARRAY['padrao','default','is_default']);
        p_calc  := sync_export._pick(t, ARRAY['calc_c_oper','calc_coper','calculo_c_oper','calcoper','calcula']);
        p_tipo  := sync_export._pick(t, ARRAY['tipo_lancamento','tipo_operacao','tipooperacao']);
        p_oper  := sync_export._pick(t, ARRAY['ope','oper','operador','operacao_aux']);
        p_ativo := sync_export._pick(t, ARRAY['ativo','habilitado','status']);

        IF p_code IS NULL OR p_desc IS NULL THEN
            RAISE NOTICE 'sync_export.plano_historico pulada (tabela % sem codigo/descricao).', t;
        ELSE
            pairs := format('''codigo'', h.%I::text, ''descricao'', h.%I', p_code, p_desc);
            IF p_ana   IS NOT NULL THEN pairs := pairs || format(', ''analitico'', h.%I::text', p_ana); END IF;
            IF p_ta    IS NOT NULL THEN pairs := pairs || format(', ''tipo_ta'', h.%I::text', p_ta); END IF;
            IF p_es    IS NOT NULL THEN pairs := pairs || format(', ''natureza_es'', h.%I::text', p_es); END IF;
            IF p_padr  IS NOT NULL THEN pairs := pairs || format(', ''padrao'', h.%I::text', p_padr); END IF;
            IF p_calc  IS NOT NULL THEN pairs := pairs || format(', ''calc_c_oper'', h.%I::text', p_calc); END IF;
            IF p_tipo  IS NOT NULL THEN pairs := pairs || format(', ''tipo'', h.%I::text', p_tipo); END IF;
            IF p_oper  IS NOT NULL THEN pairs := pairs || format(', ''operacao'', h.%I::text', p_oper); END IF;
            IF p_ativo IS NOT NULL THEN pairs := pairs || format(', ''ativo'', h.%I::text', p_ativo); END IF;

            sql := 'CREATE OR REPLACE VIEW sync_export.plano_historico AS SELECT '
                || format('h.%I::text', p_code) || ' AS entity_key, '
                || fixed || ' AS occurred_at_utc, jsonb_build_object(' || pairs || ')::text AS payload_json, '
                || format('(''arpa-plano-hist-'' || h.%I)::text', p_code) || ' AS trace_id '
                || format('FROM public.%I h WHERE h.%I IS NOT NULL', t, p_code);
            EXECUTE sql;
            RAISE NOTICE 'sync_export.plano_historico criada (tabela %; occurred_at fixo).', t;
        END IF;
    END IF;
  EXCEPTION WHEN OTHERS THEN
    RAISE NOTICE 'sync_export.plano_historico pulada (%).', SQLERRM;
  END;
END;
$cob$;
