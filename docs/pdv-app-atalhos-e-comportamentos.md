# PDV App — Referência de atalhos e comportamentos de UX

Documento técnico de referência para construção do sistema de ajuda integrado ao
PDV App. Descreve cada atalho, o comportamento esperado, a implementação no código
e as regras de negócio associadas.

> **Atualizado em 2026-07-20** (sprint 1.2.0). Onde o comportamento mudou em
> relação a uma versão anterior deste documento, isso é marcado explicitamente
> no texto — não presuma que uma seção sem essa marca está correta sem
> conferir a data da última alteração no git (`git log -- <arquivo>`).

## Novidades da sprint 1.2.0 (2026-07-19/20)

- **Layout**: linha de lançamento com alturas uniformes (`SaleEntryInputStyle`).
- **Venda direta por match exato**: `PdvValidation.TryPickExactMatch` (id,
  barras, sku, chave externa, código de fábrica).
- **Cliente no pagamento**: CPF/CNPJ, código interno ou lupa
  (`CustomerSearchWindow`) — ver seção "Campo Cliente" abaixo.
- **Parcelas**: `PdvInstallmentCalculator` + grade editável no `PaymentWindow`
  — ver seção "Grade de parcelas" abaixo.
- **Unidades de medida**: `UnitSelectionDialog` quando o produto vende em mais
  de uma unidade; preço por unidade no F3.
- **Caixa**: resumo em grid, observação obrigatória em sangria/suprimento,
  fechamento cego por espécie, relatório A4/térmica.
- **F8 → SaleDetailWindow**: substitui o painel de cancelamento inline por uma
  tela de detalhe com reimpressão e cancelamento.
- Contrato de sincronização em `1.11.0` (`../sync`): snapshot de clientes,
  `units[]` no produto, `installments_plan` e unidade nos itens do evento de
  venda.

---

## Atalhos de teclado globais (Window.KeyDown)

Implementados no handler `MainWindow_KeyDown` (`MainWindow.xaml.cs`).
Disparam de qualquer campo da janela.

### F1 — Ajuda

- **Condição:** sempre disponível.
- Abre `HelpWindow` via `HelpButton_Click`.
- Exibe atalhos globais, atalhos por campo e sinais visuais em três DataGrids.
- Comportamento idêntico ao botão **F1 Ajuda** no cabeçalho.

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

> **Nota (2026-07-20):** o login por `OperatorLoginTextBox` e a autorização de
> supervisor "por turno" descritos nas versões anteriores deste documento
> foram removidos na sprint de 2026-07-18. O login agora acontece em
> `LoginWindow`, antes da `MainWindow` existir (ver `App.xaml.cs`), e cada
> operação sensível abre `SupervisorAuthorizationDialog` (login + senha +
> motivo) sob demanda — não existe mais autorização válida para o turno
> inteiro. Ver seção "Autorização de supervisor" mais abaixo, já atualizada.

### Valor de abertura (`OpeningAmountTextBox`)

- **Enter** → equivalente a clicar "Abrir caixa".
- Cria nova `pdv.cash_sessions` com `status='open'` e `opening_amount` informado.
- Se já existe sessão aberta para o operador: reutiliza sem criar nova.

### Fechamento do caixa (`ClosingCountsGrid`) — contagem cega por espécie

*(substituiu o antigo `ClosingAmountTextBox` único na sprint 1.2.0, 2026-07-19)*

- Grid carregado com uma linha por espécie de pagamento ativa
  (`PdvPaymentCatalogRepository.GetActiveSpeciesAsync`, "Dinheiro" primeiro).
- Coluna **Contado** é editável; as demais (nome, esperado) **não aparecem**
  na tela — o fechamento é intencionalmente cego.
- Ao clicar **Fechar caixa**: `PdvCashClosing.BuildClosingCounts` (puro,
  `PdvLocal.Core`) casa a contagem com o resumo da sessão e resolve
  esperado/diferença por espécie; `closing_amount` = contado da espécie
  `kind == "cash"`; tudo persistido em `pdv.cash_sessions.closing_counts`
  (jsonb).
- Abre `CashCloseReportWindow` automaticamente após fechar, com os valores
  esperado/contado/diferença revelados.

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

### Diálogo de autorização (`SupervisorAuthorizationDialog`)

- Aberto sob demanda por `SupervisorAuthorizationDialog.Request(owner, operatorRepository, descrição)`
  a partir de qualquer operação sensível (desconto, remoção de item/pagamento,
  cancelamento de venda, sangria/suprimento).
- Campos: **login + senha** (validados via `AuthenticateAsync`, mesmo hash
  pbkdf2 do login principal) **+ motivo**.
- Retorna `SupervisorAuthorization(Supervisor, Reason)` ou `null` se cancelado.
- **Não persiste entre operações** — cada chamada exige login+senha+motivo de
  novo, mesmo para o mesmo supervisor na mesma sessão.

### Campo Cliente (`CustomerDocumentTextBox`, `PaymentWindow`)

*(sprint 1.2.0)*

- **Enter** ou **perda de foco** → `ResolveCustomerDocumentAsync`.
- `PdvValidation.ClassifyCustomerLookupInput` decide o tratamento:
  - 11/14 dígitos (com ou sem pontuação) → CPF/CNPJ →
    `PdvCustomerRepository.FindActiveByDocumentAsync`.
  - 1 a 10 dígitos → código interno do ERP →
    `PdvCustomerRepository.FindActiveByCodeAsync`; não encontrado lança erro
    explícito ("Cliente com código X não encontrado").
  - Qualquer outra coisa → erro "Informe CPF/CNPJ ou o código interno".
- Botão **lupa** (`CustomerSearchButton`) abre `CustomerSearchWindow`
  (busca por nome/documento/código, Enter ou duplo clique seleciona).
- Guard `_resolvingCustomer` evita reentrância entre o LostFocus e o Enter.

### Grade de parcelas (`InstallmentsGrid`, `PaymentWindow`)

*(sprint 1.2.0)*

- Visível apenas quando o pagamento selecionado em `PaymentsGrid` tem
  `InstallmentsPlan` (condição com `installments > 1` ou `first_due_days > 0`).
- Coluna **Vencimento** editável (`dd/MM/yyyy`); coluna **Valor** somente
  leitura.
- Trocar a seleção em `PaymentsGrid` salva a edição em memória
  (`SaveDisplayedInstallmentEdits`) antes de trocar a grade exibida.
- Ao concluir a venda, `ApplyEditedInstallmentPlans` reconfirma a edição
  aberta e valida todos os planos com `PdvInstallmentCalculator.ValidatePlan`
  (numeração 1..N, soma bate com o valor do pagamento, datas não
  decrescentes).

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
| `accepted` | "Sincronizado com o ERP." | DarkGreen | Para |
| `sent` | "Enviado ao ERP — aguardando confirmação." | DarkBlue | Continua |
| `rejected` | "Erro na sincronização. Verifique o SyncAgent." | DarkRed | Para |
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

### Seleção de venda → SaleDetailWindow

*(sprint 1.2.0 — substituiu o painel de cancelamento inline descrito nas
versões anteriores deste documento)*

- `SalesGrid_SelectionChanged` abre `SaleDetailWindow` (`ShowDialog`) para
  qualquer linha selecionada, independente do status.
- Guard `_openingDetail` evita reentrância quando a seleção é limpa
  (`SalesGrid.SelectedItem = null`) ao fechar o diálogo.
- Se `SaleDetailWindow.SaleChanged == true` (venda foi cancelada), a lista é
  recarregada (`LoadSalesAsync`).

**Linhas canceladas:** exibidas com `Foreground="#94A3B8"` e `FontStyle="Italic"` via `DataTrigger` em `DataGrid.RowStyle` quando `IsCancelled == true`.

**Totais do rodapé:** somam apenas vendas com `Status == "completed"` (canceladas são excluídas da contagem e do total).

### SaleDetailWindow (detalhe, reimpressão e cancelamento)

*(sprint 1.2.0)*

- Carrega `PdvSaleRepository.GetCompletedSaleDetailAsync(saleId)` — 3 SELECTs
  (cabeçalho com operador/cliente, itens com unidade congelada, pagamentos
  com `installments_plan` do payload).
- **Reimprimir**: reconstrói `SaleReceiptData` a partir do detalhe (não do
  estado em memória da venda) e abre `SaleReceiptWindow`. Troco não é
  persistido em `pdv.payments` → sempre sai como zero na reimpressão.
- **Cancelar venda**: só visível quando `Status == "completed"`; abre
  `SupervisorAuthorizationDialog` e chama `CancelSaleAsync` (mesma lógica de
  antes: UPDATE + INSERT auditoria, transação atômica). Após cancelar,
  recarrega o detalhe (mostra "CANCELADA" em vermelho) e marca
  `SaleChanged = true` para a janela de origem recarregar a lista.

### SQL utilizado

```sql
SELECT
    s.sale_id,
    s.sale_number,
    s.completed_at_utc,
    s.total_amount,
    s.sync_status,
    s.status,
    COUNT(DISTINCT si.sale_item_id)::int           AS item_count,
    COALESCE(STRING_AGG(DISTINCT p.payment_method, ', '), '-') AS payment_methods
FROM pdv.sales s
LEFT JOIN pdv.sale_items si ON si.sale_id = s.sale_id
LEFT JOIN pdv.payments   p  ON p.sale_id  = s.sale_id
WHERE s.cash_session_id = @cash_session_id
  AND s.status != 'draft'
GROUP BY s.sale_id, s.sale_number, s.completed_at_utc, s.total_amount, s.sync_status, s.status
ORDER BY s.completed_at_utc DESC
```

---

## HelpWindow

### Abertura

- Atalho: **F1** (global, qualquer campo) via `MainWindow_KeyDown`.
- Botão **F1 Ajuda** no cabeçalho da janela principal via `HelpButton_Click`.
- Janela modal em relação à janela principal (`ShowDialog`).

### Estrutura de dados

Arquivo `HelpWindow.xaml.cs` — três listas estáticas (atualizadas na sprint
1.2.0; conteúdo completo direto no código-fonte, não duplicado aqui para não
ficar dessincronizado de novo):

- **GlobalShortcuts** — `List<HelpRow>` (Key/Condition/Action): F1-F4, F8-F11,
  Ctrl+D, Ctrl+L, Esc.
- **FieldShortcuts** — inclui, desde 1.2.0: seleção de unidade (1-9 ou
  setas+Enter), campo Cliente (Enter resolve, lupa pesquisa), edição de
  parcela na grade, abertura do detalhe de venda a partir do F8, e a grade de
  fechamento cego do caixa. As linhas antigas de "Login supervisor
  (cancel.)"/"Motivo (cancelamento)" (painel inline removido) foram retiradas.
- **VisualCues** — inclui, desde 1.2.0: status "Finalizada"/"CANCELADA" na
  tela de detalhe e o aviso de que a diferença do fechamento só aparece no
  relatório (contagem cega).

**Dica do rodapé:** corrigida na 1.2.0 — não existe mais autorização de
supervisor "por turno" (ver seção de autorização abaixo); foi acrescentada
uma segunda dica sobre venda a prazo exigir cliente cadastrado.

### `HelpRow` record

```csharp
internal sealed record HelpRow(string Key, string Condition, string Action)
{
    internal HelpRow(string key, string action) : this(key, string.Empty, action) { }
}
```

### Estrutura XAML

Arquivo `HelpWindow.xaml` — janela 640×560, três `DataGrid` em `ScrollViewer` vertical, sem `AutoGenerateColumns`. Headers: "ATALHOS GLOBAIS", "ATALHOS POR CAMPO", "SINAIS VISUAIS".

---

## Auto-refresh do status

- `DispatcherTimer` com intervalo de **60 segundos**.
- Inicia em `Window_Loaded`, após o primeiro refresh manual.
- Para em `OnClosed` (descarte limpo do timer).
- Guard `_isRefreshingStatus` impede execuções simultâneas (timer + botão manual).
- Atualiza banner, cards de status e base de dados local.

---

## Autorização de supervisor — por operação (desde 2026-07-18)

> Substitui o modelo antigo "uma autorização por turno" (removido na sprint
> de login/caixa/pagamento de 2026-07-18). Descrito aqui porque várias
> versões anteriores deste documento ainda traziam o modelo antigo.

### Fluxo atual

```
Cada operação sensível → SupervisorAuthorizationDialog.Request(owner, operatorRepository, descrição)
  → supervisor digita login + senha + motivo
  → validado (AuthenticateAsync + IsSupervisorRole) a cada chamada
  → autorização vale só para aquela operação; não fica guardada em memória
```

### O que exige autorização (uma por operação, sempre)

- Desconto no item (campo Desc. item > 0)
- Remoção de item do carrinho
- Remoção de pagamento
- Suprimento e sangria de caixa (desde 1.2.0, além do motivo do supervisor, o
  operador preenche uma **Observação** própria, gravada no `reason` do
  movimento)
- Cancelamento de venda finalizada (desde 1.2.0, via `SaleDetailWindow`)

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
| "Informe a observação do suprimento/sangria..." *(1.2.0)* | Campo Observação vazio | Preencher a observação antes de confirmar |
| "Venda a prazo exige cliente cadastrado..." *(1.2.0)* | Condição com parcelas/vencimento futuro sem cliente resolvido | Resolver o cliente (documento, código ou lupa) antes de adicionar o pagamento |
| "Cliente com código X não encontrado." *(1.2.0)* | Código interno digitado não existe em `pdv.customers` | Conferir o código ou usar a lupa |
| "Informe CPF/CNPJ ou o código interno do cliente." *(1.2.0)* | Texto no campo Cliente não é documento nem código válido | Corrigir a entrada |
| "Vencimento da parcela N inválido: use o formato dd/mm/aaaa." *(1.2.0)* | Data digitada na grade de parcelas em formato errado | Corrigir a data |
| "Soma das parcelas (...) difere do valor do pagamento (...)." *(1.2.0)* | Edição manual do plano de parcelas quebrou o total | Ajustar os valores das parcelas |
