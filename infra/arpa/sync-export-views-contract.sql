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

-- View: sync_export.vendas
-- Colunas obrigatorias: entity_key text, occurred_at_utc timestamptz, payload_json text
-- payload_json JA no formato consumido por sync_api.domain_processor.apply_arpa_venda:
--   codigo_venda_arpa, data (timestamptz), status, vendedor_codigo, vendedor_nome,
--   desconto_total, condicao_pagamento, especie_pagamento/forma_pagamento,
--   numero_nota, cliente_documento, cliente_nome, empresa_cnpj, empresa_nome,
--   loja_codigo (o agente injeta o loja_codigo da conexao se ausente),
--   itens: [ { codigo_produto_arpa, quantidade, valor_unitario, desconto } ].
-- O schema legado do Arpa (pedidos/itenspedido) varia MUITO por cliente -
-- ajuste tabelas/colunas ao real. O agente/ERP tratam a ausencia de campos.
/*
CREATE OR REPLACE VIEW sync_export.vendas AS
SELECT
    v.codigo::text AS entity_key,
    COALESCE(v.updated_at_utc, v.data, now())::timestamptz AS occurred_at_utc,
    jsonb_build_object(
        'codigo_venda_arpa', v.codigo,
        'data', COALESCE(v.data, v.updated_at_utc),
        'status', v.status,
        'vendedor_codigo', v.codvendedor,
        'desconto_total', v.desconto,
        'especie_pagamento', v.forma_pagamento,
        'cliente_documento', c.cnpj_cpf,
        'cliente_nome', c.nome,
        'itens', COALESCE((
            SELECT jsonb_agg(jsonb_build_object(
                'codigo_produto_arpa', i.codproduto,
                'quantidade', i.quantidade,
                'valor_unitario', i.valorunitario,
                'desconto', i.desconto
            ))
            FROM public.itenspedido i WHERE i.codpedido = v.codigo
        ), '[]'::jsonb)
    )::text AS payload_json,
    concat('arpa-venda-', v.codigo)::text AS trace_id
FROM public.pedidos v
LEFT JOIN public.clientes c ON c.codigo = v.codcliente
WHERE v.codigo IS NOT NULL;
*/

-- View: sync_export.financeiro
-- Colunas obrigatorias: entity_key text, occurred_at_utc timestamptz, payload_json text
-- payload_json JA no formato consumido por sync_api.domain_processor.apply_financeiro:
--   titulo_externo_id, natureza ('receber' -> TituloReceber | 'pagar' -> TituloPagar;
--   ausente = receber), e conforme a natureza:
--     receber: codigo_venda_arpa, cliente_documento, cliente_nome, cliente_codigo,
--       especie_pagamento/forma_pagamento, valor_base, multa, juros,
--       valor_recebido/valor_pago, valor_atual, vencimento, data_recebimento,
--       status, nosso_numero, linha_digitavel, codigo_barras, url_boleto.
--     pagar: compra_externa_id, documento, fornecedor_codigo, fornecedor_nome,
--       emissao, vencimento, valor_base, multa, juros, valor_pago, data_pagamento,
--       historico_codigo, historico_analitico, status, tem_quitacao.
-- entity_key do pagar deve levar prefixo 'pag-' para nao colidir com um titulo
-- a receber de mesmo codigo (a view do agente une as duas fontes com UNION ALL).
/*
CREATE OR REPLACE VIEW sync_export.financeiro AS
SELECT
    r.codigo::text AS entity_key,
    COALESCE(r.updated_at_utc, r.datapagamento, r.vencimento, now())::timestamptz AS occurred_at_utc,
    jsonb_build_object(
        'titulo_externo_id', r.codigo,
        'natureza', 'receber',
        'codigo_venda_arpa', r.codpedido,
        'cliente_documento', c.cnpj_cpf,
        'cliente_nome', c.nome,
        'especie_pagamento', r.forma_pagamento,
        'valor_base', r.valor,
        'valor_recebido', r.valorpago,
        'vencimento', r.vencimento,
        'data_recebimento', r.datapagamento,
        'status', r.status,
        'documento', r.documento
    )::text AS payload_json,
    concat('arpa-financeiro-', r.codigo)::text AS trace_id
FROM public.contas_receber r
LEFT JOIN public.clientes c ON c.codigo = r.codcliente
WHERE r.codigo IS NOT NULL
UNION ALL
SELECT
    ('pag-' || p.codigo)::text AS entity_key,
    COALESCE(p.updated_at_utc, p.datapagamento, p.vencimento, now())::timestamptz AS occurred_at_utc,
    jsonb_build_object(
        'titulo_externo_id', p.codigo,
        'natureza', 'pagar',
        'compra_externa_id', p.codpedido,
        'documento', p.documento,
        'fornecedor_codigo', p.codfornecedor,
        'fornecedor_nome', f.nome,
        'valor_base', p.valor,
        'valor_pago', p.valorpago,
        'vencimento', p.vencimento,
        'data_pagamento', p.datapagamento,
        'emissao', p.dataemissao,
        'status', p.status
    )::text AS payload_json,
    concat('arpa-financeiro-pag-', p.codigo)::text AS trace_id
FROM public.contas_pagar p
LEFT JOIN public.clientes f ON f.codigo = p.codfornecedor
WHERE p.codigo IS NOT NULL;
*/

-- View: sync_export.cobranca   (entity_type=cobranca, contrato Sync 2.10.0)
-- Colunas obrigatorias: entity_key text, occurred_at_utc timestamptz, payload_json text
-- O ERP grava cobranca.ContaCobranca (+ cedente + vinculo Arpa + credencial
-- CNAB240) via sync_api.domain_processor.apply_cobranca -> reaproveita
-- cobranca.services.arpa._normalize_arpa_cobranca_row +
-- importar_contas_cobranca_arpa_payloads. O payload leva as chaves CRUAS (o ERP
-- infere identidade do banco, limpa digitos e monta cedente):
--   externo_id, nome_exibicao, banco_codigo, banco_nome, agencia, agencia_digito,
--   conta, conta_digito, carteira, variacao_carteira, convenio,
--   codigo_beneficiario, posto, cooperativa, sigla, instrucao_padrao,
--   cedente_nome, cedente_documento, cedente_email, cedente_telefone,
--   cedente_cidade, cedente_uf, ativo_raw, padrao_raw.
-- Obrigatorio no minimo: agencia, conta e (banco_codigo OU banco_nome).
--
-- ATENCAO: o schema de cobranca do Arpa varia muito (tabela de contas bancarias,
-- convenios, cedente as vezes em tabela separada). O bloco DO de
-- sync-export-views.sql ainda NAO cria esta view -- portar a introspecao de
-- cobranca/services/arpa.py (_find_arpa_cobranca_source /
-- _find_arpa_cobranca_join_source / _infer_bank_identity) exige validar contra
-- um Arpa real primeiro (usar export_arpa_schema_diagnostics). Enquanto isso o
-- toggle "Cobranca" fica indisponivel; o ERP ja aceita o evento.
/*
CREATE OR REPLACE VIEW sync_export.cobranca AS
SELECT
    b.codigo::text AS entity_key,
    COALESCE(b.updated_at_utc, now())::timestamptz AS occurred_at_utc,
    jsonb_build_object(
        'externo_id', b.codigo,
        'nome_exibicao', b.descricao,
        'banco_codigo', b.banco,
        'banco_nome', b.nomebanco,
        'agencia', b.agencia,
        'agencia_digito', b.agenciadv,
        'conta', b.conta,
        'conta_digito', b.contadv,
        'carteira', b.carteira,
        'convenio', b.convenio,
        'codigo_beneficiario', b.codcedente,
        'cedente_nome', b.cedente,
        'cedente_documento', b.cnpjcedente,
        'ativo_raw', b.ativo,
        'padrao_raw', b.padrao
    )::text AS payload_json,
    concat('arpa-cobranca-', b.codigo)::text AS trace_id
FROM public.contas_bancarias b
WHERE b.codigo IS NOT NULL;
*/
