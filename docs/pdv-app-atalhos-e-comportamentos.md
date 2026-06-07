# PDV App — Referência de atalhos e comportamentos de UX

Documento técnico de referência para construção do sistema de ajuda integrado ao
PDV App. Descreve cada atalho, o comportamento esperado, a implementação no código
e as regras de negócio associadas.

---

## Atalhos de teclado globais (Window.KeyDown)

Implementados no handler `MainWindow_KeyDown` (`MainWindow.xaml.cs`).
Disparam de qualquer campo da janela.

### F8 — Vendas do caixa

- **Condição:** caixa aberto (`CashSalesButton.IsEnabled == true`).
- Abre `CashSessionSalesWindow` com a lista de vendas da sessão atual.
- Comportamento idêntico ao botão **Vendas do caixa** no cabeçalho.

### F9 — Reimprimir último comprovante

- **Condição:** pelo menos uma venda finalizada na sessão (`_lastReceipt is not null`).
- Abre nova instância de `SaleReceiptWindow` com os dados da última venda.
- Comportamento idêntico ao botão **Reimprimir** no cabeçalho.

### F2 — Nova venda

- **Condição sem itens/pagamentos**: limpa o estado da venda sem pedir autorização.
- **Condição com itens ou pagamentos em aberto**: exige autorização de supervisor
  (campo login + motivo) antes de limpar.
- **Comportamento idêntico** ao botão "Nova venda".

### F12 — Finalizar venda

- Valida: caixa aberto, pelo menos 1 item, valor pago igual ao total.
- Grava a venda em transação atômica: `pdv.sales`, `pdv.sale_items`,
  `pdv.payments`, `pdv.operation_audits`.
- Muda `sync_status` para `pending_sync`.
- Limpa o carrinho e foca o campo de produto.
- **Comportamento idêntico** ao botão "Finalizar venda".

### Esc — Cancelar seleção de produto

- **Ativa apenas** quando há um produto selecionado (`_selectedProduct is not null`).
- Limpa: `_selectedProduct`, `_productSearchResults`, `ProductEntryTextBox`,
  `UnitPriceTextBox`, `ItemDiscountTextBox`.
- Retorna o foco ao campo de produto.

---

## Atalhos por campo

### Login do operador (`OperatorLoginTextBox`)

- **Enter** → equivalente a clicar "Carregar".
- Busca operador ativo em `pdv.operators` pelo login informado.
- Se encontrado: carrega sessão aberta existente (se houver) ou aguarda abertura.
- Se não encontrado: mensagem de erro em vermelho, campos limpos.

### Valor de abertura (`OpeningAmountTextBox`)

- **Enter** → equivalente a clicar "Abrir caixa".
- Cria nova `pdv.cash_sessions` com `status='open'` e `opening_amount` informado.
- Se já existe sessão aberta para o operador: reutiliza sem criar nova.

### Valor de fechamento (`ClosingAmountTextBox`)

- **Enter** → equivalente a clicar "Fechar caixa".
- Proibido com venda em andamento (carrinho não vazio).
- Calcula diferença entre valor informado e `expected_cash_amount` do resumo.
- Grava `status='closed'` com notas de fechamento em formato auditável.

### Campo de produto (`ProductEntryTextBox`)

- **Enter (produto não selecionado)** → executa busca.
  - 1 resultado: seleciona automaticamente E adiciona ao carrinho.
  - Múltiplos resultados: exibe grid de resultados, aguarda seleção.
  - 0 resultados: mensagem de erro "Produto não encontrado no catálogo local."
- **Enter (produto já selecionado)** → adiciona ao carrinho diretamente.
- **Esc** → cancela seleção atual (ver atalho global).

**Regra de busca:** a auto-seleção e o auto-adicionamento ao carrinho na primeira
tecla Enter só ocorrem quando **exatamente 1 produto** é retornado. Com múltiplos
resultados, o operador deve confirmar explicitamente qual produto deseja.

### Grid de resultados de busca (`ProductSearchResultsGrid`)

- **↑ / ↓** → navegação entre linhas (comportamento nativo do DataGrid).
- **Enter** → seleciona a linha destacada (equivalente ao duplo clique).
- **Duplo clique** → seleciona o produto clicado.
- Após seleção: foco vai para `QuantityTextBox` com valor "1" selecionado.

### Quantidade (`QuantityTextBox`)

- **Enter** → adiciona o produto selecionado ao carrinho.
- O texto "1" é pré-selecionado ao focar: digitar um número substitui imediatamente.
- **Tab** pula o campo de preço (somente leitura, `IsTabStop="False"`) e vai
  direto para **Desconto do item**.

### Desconto do item (`ItemDiscountTextBox`)

- **Enter** → adiciona o produto selecionado ao carrinho.
- Se valor > 0: exige supervisor autorizado e motivo preenchido.
- Fundo muda para **âmbar (#FEF3C7)** quando desconto > 0.

### Carrinho (`CartItemsGrid`)

- **Delete** → remove o item selecionado (equivalente ao botão "Remover item").
- Sempre exige autorização de supervisor e motivo.
- Após remoção: itens são renumerados e totais recalculados.

### Login do supervisor (`SupervisorLoginTextBox`)

- **Enter** → equivalente a clicar "Autorizar".
- Valida que o operador existe, está ativo e tem papel `supervisor` ou `admin`.
- Autorização fica ativa durante toda a sessão do caixa.
- Limpa apenas quando um novo operador é carregado (`LoadOperatorAsync`).

### Valor recebido (`PaymentReceivedTextBox`)

- **GotFocus** → preenche automaticamente com o valor restante a pagar, se o
  campo estiver zerado e houver itens no carrinho.
- O texto é selecionado para facilitar substituição.
- **Enter** → adiciona o pagamento (equivalente ao botão "Adicionar").

### Grade de pagamentos (`PaymentsGrid`)

- **Delete** → remove o pagamento selecionado (equivalente ao botão "Remover").
- Sempre exige autorização de supervisor e motivo.

---

## Comportamentos de UX e feedback visual

### Cor do campo "Restante"

- **Vermelho (#DC2626)** quando `remaining > 0` (venda não totalmente paga).
- **Preto (#0F172A)** quando `remaining == 0` (venda pronta para finalização).
- Atualiza a cada mudança no carrinho ou nos pagamentos (`UpdateTotals`).

### Cor do status da venda (`SaleGateValue`)

- **Verde (#166534)** quando há operador e caixa abertos.
- **Cinza (#64748B)** quando o caixa está fechado ou sem operador.
- Atualiza em `ApplySaleGate`, chamado sempre que a sessão muda.

### Cor da autorização do supervisor (`SupervisorAuthorizationValue`)

- **Verde (#166534)** quando supervisor está autorizado na sessão.
- **Cinza (#64748B)** quando aguardando autorização.
- Persiste verde entre vendas (autorização de sessão).

### Título dinâmico da seção de venda (`SaleTitleValue`)

- **"Venda"** quando o carrinho está vazio.
- **"Venda — N itens — R$ X,XX"** quando há itens no carrinho.
- Atualiza em `UpdateTotals`.

### Scroll automático no carrinho

- Após cada item adicionado, o carrinho rola automaticamente para exibir o
  último item (`CartItemsGrid.ScrollIntoView`).

### Foco após seleção de produto

- Após seleção (duplo clique ou grid), foco vai para `QuantityTextBox` com
  valor pré-selecionado.
- Após adição ao carrinho, foco retorna ao `ProductEntryTextBox`.

### Campo "Desc. total"

- Fundo **âmbar (#FEF3C7)** quando desconto total > 0.
- Retorna ao branco em "Nova venda" ou ao zerar o valor.

### Grids de busca e pagamentos com altura dinâmica

- `ProductSearchResultsGrid`: `MaxHeight="180"`. Colapsa quando vazio, cresce
  conforme resultados (máx. ~8 linhas visíveis).
- `PaymentsGrid`: `MaxHeight="160"`. Cresce conforme pagamentos adicionados.

---

## Comprovante de venda (`SaleReceiptWindow`)

### Quando abre

Automaticamente após `CompleteSaleButton_Click` completar com sucesso, via
`new SaleReceiptWindow(receipt) { Owner = this }.Show()`.

A janela é não-modal (`Show`, não `ShowDialog`): o operador pode iniciar a próxima
venda enquanto o comprovante ainda está visível.

### Dados exibidos

| Campo | Origem |
|-------|--------|
| Número da venda | `SaleReceiptData.SaleNumber` (`PDV-yyyyMMddHHmmss`) |
| Data/hora | `SaleReceiptData.CompletedAt` (capturado antes de `ResetSale`) |
| Operador | `_currentOperator.DisplayName` |
| Itens | `_saleItems.ToList()` (snapshot antes de `ResetSale`) |
| Descontos | Calculados em `BuildReceiptData` |
| Pagamentos | `_payments.ToList()` (snapshot antes de `ResetSale`) |
| Troco | Soma de `UiPayment.ChangeAmount` de todos os pagamentos |

### Linhas de desconto por item

- Linha de desconto aparece abaixo do nome do item quando `DiscountAmount > 0`.
- Controlada por `Visibility` binding em `ReceiptItemRow.HasDiscount`.

### Linhas de desconto condicionais

- `ItemDiscountRow` (`Visibility.Collapsed`) quando `ItemDiscountAmount == 0`.
- `SaleDiscountRow` (`Visibility.Collapsed`) quando `SaleDiscountAmount == 0`.
- `ChangeRow` (`Visibility.Collapsed`) quando `TotalChangeAmount == 0`.

### Polling de status de sync

- `DispatcherTimer` com intervalo de **8 segundos**, iniciado no construtor.
- A cada tick: chama `PdvSaleRepository.GetSyncStatusAsync(receipt.SaleId)`.
- Resultados:

| Status retornado | Texto exibido | Cor | Timer |
|-----------------|---------------|-----|-------|
| `synced` | "Sincronizado com o ERP." | DarkGreen | Para |
| `sync_error` | "Erro na sincronizacao. Verifique o SyncAgent." | DarkRed | Para |
| outros | *(sem alteração — continua "Aguardando...")* | — | Continua |

- Timer para em `OnClosed` (descarte limpo).
- Exceções no poll são silenciosas — não interrompem a UX.
- `SaleId` vem de `SaleReceiptData.SaleId` (campo adicionado junto com o polling).

### Impressão

- Botão "Imprimir" abre `PrintDialog` do Windows.
- Imprime `ReceiptPanel` (o `StackPanel` com o conteúdo) via `PrintVisual`.
- Compatível com qualquer impressora Windows, incluindo térmicas de 80mm.
- O título do job de impressão é o número da venda.

### Captura dos dados (ordem de operações em `CompleteSaleButton_Click`)

```
1. CreateCompletedSaleAsync  ← grava no banco
2. BuildReceiptData          ← snapshot do estado atual (antes de limpar)
3. ResetSale                 ← limpa carrinho, pagamentos, supervisor
4. SetOperationMessage
5. RefreshCashSummaryAsync
6. RefreshDatabaseStatusAsync
7. FocusProductEntry
8. Show SaleReceiptWindow    ← janela não-bloqueante
```

---

## Rascunho de venda (draft persistence)

### Quando é salvo

- Após cada chamada a `AddSelectedProductToCart` (item adicionado).
- Após cada chamada ao fluxo de remoção em `RemoveItemButton_Click` (item removido).
- O método `SaveDraftToBackground()` captura snapshot de `_saleItems` e `SaleDiscountTextBox.Text` na UI thread e delega a escrita para `Task.Run` (fire-and-forget silencioso).

### Quando é apagado

- Início de `ResetSale` — via `DeleteDraftInBackground()` (fire-and-forget silencioso).
- Cobre: venda finalizada, nova venda com F2, app fechado após venda concluída.

### Quando é restaurado

- Imediatamente após `_currentCashSession` ser definido (sessão existente carregada ou nova sessão aberta).
- Chama `LoadDraftIfAvailableAsync()` → `GetDraftAsync(cashSessionId)`.
- Se encontrado: reconstrói `UiSaleItem` para cada item, restaura `SaleDiscountTextBox`, chama `UpdateTotals()`, exibe mensagem "Rascunho restaurado: N itens da venda anterior."

### Armazenamento

- Tabela: `pdv.sales` com `status = 'draft'`, `sync_status = 'not_published'`.
- `sale_number`: `'RASCUNHO-{cashSessionId[..8]}'` (único por sessão via DELETE + INSERT em transação).
- Itens: `pdv.sale_items` (ON DELETE CASCADE, apagados junto com o rascunho).
- Desconto total: `pdv.sales.discount_amount`.

### Comportamento de falha

- Salvar e apagar são fire-and-forget. Falhas não interrompem a UX.
- Carregar falha silenciosamente — se o draft não puder ser lido, o carrinho inicia vazio.

---

## Botões do cabeçalho

### Reimprimir

- **Atalho:** F9 (quando `_lastReceipt is not null`)
- **Nome:** `ReprintButton`
- **Visibilidade inicial:** `Collapsed` — aparece somente após a primeira venda finalizada.
- **Clique:** abre `new SaleReceiptWindow(_lastReceipt) { Owner = this }.Show()`.
- **Campo:** `_lastReceipt` (`SaleReceiptData?`) é definido em `CompleteSaleButton_Click`
  imediatamente após `BuildReceiptData`, antes de `ResetSale`.
- O campo persiste enquanto o app está aberto; não é redefinido entre vendas.

### Vendas do caixa

- **Atalho:** F8 (quando caixa aberto)
- **Nome:** `CashSalesButton`
- **Estado:** habilitado em `ApplySaleGate` quando `_currentCashSession is not null`.
- **Clique:** instancia `CashSessionSalesWindow(saleRepository, cashSessionId, sessionTitle)`.
  - `sessionTitle` = `"NomeOperador — aberto em dd/MM/yyyy HH:mm"`.
- A janela é não-modal (`Show`): pode ser aberta enquanto uma venda está em andamento.

---

## CashSessionSalesWindow

### Dados exibidos

DataGrid com uma linha por venda, carregado via `PdvSaleRepository.GetByCashSessionAsync`:

| Coluna | Fonte |
|--------|-------|
| Número | `sale_number` |
| Hora | `completed_at_utc` convertido para fuso local, formato `HH:mm` |
| Itens | `COUNT(DISTINCT sale_item_id)` |
| Total | `total_amount` formatado como moeda |
| Pagamentos | `STRING_AGG(DISTINCT payment_method, ', ')` |
| Sync | `sync_status` com cor semântica |

### Mapeamento de status na listagem

Quando a venda está cancelada (`Status == "cancelled"`), exibe "Cancelado" em cinza independente do `sync_status`.
Para vendas ativas, o mapeamento é:

| `sync_status` | Label | Cor |
|---------------|-------|-----|
| `accepted` | Sincronizado | Verde (#166534) |
| `sent` | Enviado | Azul (#1E40AF) |
| `pending_sync` | Pendente | Âmbar (#92400E) |
| `rejected` | Rejeitado | Vermelho (#B91C1C) |
| outros | valor bruto | Cinza (#64748B) |

### Rodapé

- Contagem: `"N vendas"` (ou `"Nenhuma venda nesta sessao."`)
- Total: soma de `TotalAmount` formatada como `"Total: R$ X.XXX,XX"`

### Cancelamento de venda

Ativado quando o usuário seleciona uma linha com `Status == "completed"` no DataGrid.

**Painel de cancelamento (`CancellationPanel`):**
- Aparece em `Grid.Row="2"` (entre o DataGrid e o rodapé), fundo âmbar.
- Campos: Login supervisor + Motivo.
- Enter no campo Login → foco vai para Motivo.
- Enter no campo Motivo → executa `ExecuteCancellationAsync`.
- "Confirmar cancelamento" (vermelho) → `ExecuteCancellationAsync`.
- "Descartar" → oculta painel, deseleciona linha.

**Fluxo de `ExecuteCancellationAsync`:**
1. Valida campos não vazios.
2. `_operatorRepository.FindActiveByLoginAsync(login)` → supervisor existe e está ativo.
3. `PdvValidation.IsSupervisorRole(supervisor.Role)` → papel válido.
4. `_saleRepository.CancelSaleAsync(saleId, cashSessionId, operatorId, supervisorId, reason)`.
5. Oculta painel e recarrega a lista.

**`CancelSaleAsync` (SQL):**
```sql
UPDATE pdv.sales
SET status = 'cancelled', cancelled_at_utc = now(), sync_status = 'pending_sync', updated_at_utc = now()
WHERE sale_id = @sale_id AND status = 'completed'
-- + INSERT INTO pdv.operation_audits (operation_type = 'cancel_sale')
```
- Transação atômica: UPDATE + INSERT auditoria.
- Lança `InvalidOperationException` se `UPDATE` não afetar nenhuma linha.

**Linhas canceladas:** exibidas com `Foreground="#94A3B8"` e `FontStyle="Italic"` via `DataTrigger` em `DataGrid.RowStyle` quando `IsCancelled == true`.

**Totais do rodapé:** somam apenas vendas com `Status == "completed"` (canceladas são excluídas da contagem e do total).

### SQL utilizado

```sql
SELECT
    s.sale_id,
    s.sale_number,
    s.completed_at_utc,
    s.total_amount,
    s.sync_status,
    COUNT(DISTINCT si.sale_item_id)::int           AS item_count,
    COALESCE(STRING_AGG(DISTINCT p.payment_method, ', '), '-') AS payment_methods
FROM pdv.sales s
LEFT JOIN pdv.sale_items si ON si.sale_id = s.sale_id
LEFT JOIN pdv.payments   p  ON p.sale_id  = s.sale_id
WHERE s.cash_session_id = @cash_session_id
GROUP BY s.sale_id, s.sale_number, s.completed_at_utc, s.total_amount, s.sync_status
ORDER BY s.completed_at_utc DESC
```

---

## Auto-refresh do status

- `DispatcherTimer` com intervalo de **60 segundos**.
- Inicia em `Window_Loaded`, após o primeiro refresh manual.
- Para em `OnClosed` (descarte limpo do timer).
- Guard `_isRefreshingStatus` impede execuções simultâneas (timer + botão manual).
- Atualiza banner, cards de status e base de dados local.

---

## Autorização de supervisor — escopo de sessão

### Fluxo único no início do turno

```
Supervisor: digita login → Enter → autorizado para a sessão
Operador: para cada desconto, preenche apenas o Motivo
```

### O que limpa a autorização

| Evento | Limpa autorização? |
|--------|-------------------|
| Nova venda (F2 ou botão) | Não — apenas limpa o Motivo |
| Finalizar venda (F12) | Não — persiste para a próxima venda |
| Novo operador carregado | **Sim** — autorização pertence à sessão do operador anterior |
| Fechar e reabrir o app | **Sim** — estado em memória é perdido |

### O que a autorização permite

- Desconto no item (campo Desc. item > 0)
- Desconto total na venda (campo Desc. total > 0)
- Remoção de item do carrinho
- Remoção de pagamento
- Limpeza de venda em andamento (Nova venda com itens)
- Suprimento e sangria de caixa

---

## Ordem de tabulação (Tab) nos campos de venda

```
ProductEntryTextBox
  → QuantityTextBox        (foco automático após seleção)
  → [UnitPriceTextBox]     (pulado — IsTabStop="False", somente leitura)
  → ItemDiscountTextBox
  → ProductSearchButton
  → AddItemButton
```

---

## Mensagens de erro comuns

| Mensagem | Causa | Ação |
|----------|-------|------|
| "Produto não encontrado no catálogo local." | Código ou nome não existe no banco local | Verificar sincronização com ERP |
| "Abra ou carregue um caixa antes de vender." | Nenhum caixa aberto | Carregar operador e abrir caixa |
| "Autorização de supervisor obrigatória para..." | Supervisor não autorizado ou motivo vazio | Preencher login + motivo e clicar Autorizar |
| "Valor recebido deve ser maior que zero." | Campo Valor recebido está zerado | Informar o valor recebido |
| "A venda já está paga." | Tentativa de adicionar pagamento com restante = 0 | Nenhuma ação necessária |
| "Pagamento maior que o restante só é permitido para dinheiro." | Valor de cartão/Pix acima do restante | Informar valor exato para formas não-dinheiro |
| "Finalize ou limpe a venda em andamento antes de fechar o caixa." | Carrinho não vazio ao fechar caixa | Finalizar ou cancelar a venda atual |
| "Desconto total não pode deixar a venda negativa." | Desconto maior que o subtotal | Reduzir o desconto total |
| "Sangria não pode ser maior que o dinheiro esperado no caixa." | Valor de sangria acima do saldo | Reduzir o valor da sangria |
