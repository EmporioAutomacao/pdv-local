# PDV App — Sprint 2026-06-06: resumo do que foi implementado

## Objetivo

Reduzir o número de cliques e movimentos de mouse por venda, corrigir pontos
de fricção identificados na análise UX, e adicionar funcionalidades de consulta,
cancelamento e recuperação de vendas.

---

## 1. Melhorias de UX e teclado (P1–P12)

Todas as 12 melhorias identificadas na análise foram implementadas.

| # | Melhoria | Arquivo principal |
|---|----------|-------------------|
| P1 | Enter em "Valor recebido" e "Login supervisor" | `MainWindow.xaml.cs` |
| P2 | GotFocus pré-preenche "Valor recebido" com restante | `MainWindow.xaml.cs` |
| P3 | Foco vai para `QuantityTextBox` após selecionar produto | `MainWindow.xaml.cs` |
| P4 | Atalhos globais F2 / F12 / Esc via `Window.KeyDown` | `MainWindow.xaml.cs` |
| P5 | `ProductSearchResultsGrid`: `MaxHeight="180"` (dinâmico) | `MainWindow.xaml` |
| P6 | `CartItemsGrid.ScrollIntoView` após cada item adicionado | `MainWindow.xaml.cs` |
| P7 | `SaleTitleValue` exibe "Venda — N itens — R$ X,XX" | `MainWindow.xaml.cs` |
| P8 | Painel de detalhes técnicos substituído por `Expander` | `MainWindow.xaml` |
| P9 | Cards de monitoramento movidos para dentro do `Expander` | `MainWindow.xaml` |
| P10 | Autorização de supervisor com escopo de sessão (não limpa entre vendas) | `MainWindow.xaml.cs` |
| P11 | `PaymentsGrid`: `MaxHeight="160"` (dinâmico) | `MainWindow.xaml` |
| P12 | Fundo âmbar em `SaleDiscountTextBox` quando desconto > 0 | `MainWindow.xaml.cs` |

**Ganho estimado:** ~5–8 cliques de mouse a menos por venda. Em 200 vendas/dia,
representa 1.000–1.600 cliques a menos por turno.

---

## 2. Comprovante de venda (`SaleReceiptWindow`)

Janela não-modal exibida automaticamente após cada finalização de venda.

**O que exibe:**
- Número da venda, data/hora, operador
- Itens com quantidade, preço unitário e total
- Desconto por item e desconto total (quando aplicados, linhas condicionais)
- Formas de pagamento e valores
- Troco (linha condicional)

**Sincronização em tempo real:**  
`DispatcherTimer` com intervalo de 8 segundos faz poll de `GetSyncStatusAsync`.
Status exibido evolui: "Aguardando..." → "Enviado ao ERP..." → "Sincronizado
com o ERP." (verde). Em caso de rejeição: "Erro na sincronização." (vermelho).

**Impressão:**  
Botão "Imprimir" abre `PrintDialog` do Windows e imprime `ReceiptPanel` via
`PrintVisual`. Compatível com qualquer impressora, incluindo térmicas de 80mm.

---

## 3. Reimprimir (F9 / botão Reimprimir)

Após a primeira venda finalizada na sessão:
- Botão **Reimprimir** aparece no cabeçalho (antes fica `Collapsed`).
- F9 passa a funcionar.
- Ambos reabrem `SaleReceiptWindow` com os dados da última venda (`_lastReceipt`).

O snapshot dos dados é capturado em `BuildReceiptData` antes de `ResetSale`,
garantindo que o comprovante reflita exatamente o estado da venda finalizada.

---

## 4. Consulta de vendas do caixa (F8 / `CashSessionSalesWindow`)

Botão **Vendas do caixa** (habilitado quando há caixa aberto) abre a janela
de consulta da sessão atual. Atalho: F8.

**O que exibe:**
- Lista de todas as vendas da sessão (excluindo rascunhos)
- Colunas: Número, Hora, Itens, Total, Pagamentos, Sync
- Status de sincronização com cor semântica (Sincronizado / Enviado / Pendente / Rejeitado)
- Vendas canceladas em itálico cinza
- Rodapé com total de vendas e soma (excluindo canceladas)

---

## 5. Cancelamento de venda finalizada

Disponível dentro da `CashSessionSalesWindow`. Ao clicar em uma venda com
status "completed", o painel de cancelamento abre abaixo da lista.

**Fluxo:**
1. Login do supervisor (Enter avança para Motivo)
2. Motivo (Enter ou botão "Confirmar cancelamento")
3. Sistema valida supervisor: ativo + papel supervisor/admin
4. UPDATE atômico: `status='cancelled'`, `sync_status='pending_sync'` + auditoria
5. Lista atualizada automaticamente

O ERP é notificado na próxima janela de sincronização. O cancelamento é
irreversível no PDV Local.

---

## 6. Rascunho automático (draft persistence)

O sistema salva automaticamente o estado da venda em andamento no banco local.

**Quando salva:** após cada item adicionado ou removido (fire-and-forget silencioso).  
**Quando apaga:** ao finalizar, cancelar ou iniciar nova venda.  
**Quando restaura:** ao abrir o caixa — se houver rascunho, os itens e o desconto
total são restaurados e o operador vê a mensagem "Rascunho restaurado: N itens da
venda anterior."

Armazenamento: tabela `pdv.sales` com `status='draft'`, `sale_number='RASCUNHO-{sessionId[..8]}'`.
Itens na `pdv.sale_items` com ON DELETE CASCADE.

---

## 7. Ajuda integrada (F1 / `HelpWindow`)

Botão **F1 Ajuda** no cabeçalho (e atalho F1) abre a janela de ajuda rápida.

**Conteúdo:**
- Atalhos globais (F1–F12, Esc)
- Atalhos por campo (Enter, Delete, setas em cada área)
- Sinais visuais (significado das cores de todos os elementos da tela)

---

## 8. Novos atalhos de teclado globais

| Tecla | Condição | Ação |
|-------|----------|------|
| F1 | Sempre | Abrir HelpWindow |
| F2 | Sempre | Nova venda |
| F8 | Caixa aberto | Abrir CashSessionSalesWindow |
| F9 | Após 1ª venda | Reimprimir último comprovante |
| F12 | Sempre | Finalizar venda |
| Esc | Produto selecionado | Cancelar seleção e limpar campos |

---

## 9. Correções de bugs

| Bug | Correção |
|-----|---------|
| `selectFirstWhenSingle=true` selecionava sempre o 1º produto de qualquer busca | Removido o parâmetro; auto-seleção só quando exatamente 1 resultado |
| `PaymentConditionComboBox` nunca desabilitava com caixa fechado | Adicionado a `ApplySaleGate` |
| Sync status `"synced"` / `"sync_error"` não existem no schema | Corrigido para `"accepted"` / `"rejected"` |
| Rascunho aparecia na lista de "Vendas do caixa" | Adicionado `AND s.status != 'draft'` na query |
| Número de venda podia colidir (mesmo segundo) | Formato alterado para `PDV-yyyyMMddHHmmssfff` (milissegundos) |
| Mensagem "Rascunho restaurado" era sobrescrita | Ordem corrigida: `SetOperationMessage` antes de `LoadDraftIfAvailableAsync` |

---

## Arquivos criados

| Arquivo | Descrição |
|---------|-----------|
| `src/pdv-app/SaleReceiptWindow.xaml` | Comprovante de venda com polling de sync |
| `src/pdv-app/SaleReceiptWindow.xaml.cs` | Lógica do comprovante |
| `src/pdv-app/CashSessionSalesWindow.xaml` | Lista de vendas + painel de cancelamento |
| `src/pdv-app/CashSessionSalesWindow.xaml.cs` | Lógica de consulta e cancelamento |
| `src/pdv-app/HelpWindow.xaml` | Janela de ajuda com atalhos e sinais visuais |
| `src/pdv-app/HelpWindow.xaml.cs` | Dados estáticos da janela de ajuda |

## Arquivos modificados

| Arquivo | O que mudou |
|---------|-------------|
| `src/pdv-core/PdvModels.cs` | `PdvDraftItemCommand`, `PdvDraftItem`, `PdvDraftSale`, `PdvSaleSummary` |
| `src/pdv-core/PdvSaleRepository.cs` | `SaveDraftAsync`, `GetDraftAsync`, `DeleteDraftAsync`, `GetByCashSessionAsync`, `CancelSaleAsync`, `GetSyncStatusAsync` |
| `src/pdv-app/MainWindow.xaml` | Botões de cabeçalho, Expander técnico, handlers de teclado, MaxHeight nos grids |
| `src/pdv-app/MainWindow.xaml.cs` | Todos os novos handlers, draft, reprint, help, cancel flow |

---

## O que ficou para o futuro

- **TEF real**: integração com provedor de pagamento eletrônico (requer hardware e credenciais externas)
- **Fiscal / NF-e**: emissão de nota fiscal eletrônica (requer certificado digital e integração com SEFAZ)
