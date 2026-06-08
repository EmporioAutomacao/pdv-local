# PDV App — Análise de melhorias de UX e velocidade de venda

Data da análise: 2026-06-06 · **Implementação concluída: 2026-06-06**

## Contexto

Análise do layout e do processo de venda do PDV App (WPF) com foco em
identificar friction points que tornam o fluxo de caixa mais lento que o
necessário. A base analisada é o `MainWindow.xaml` e `MainWindow.xaml.cs`
na revisão atual da branch `main`. Todos os 12 pontos identificados foram
implementados na mesma sprint.

---

## Fluxo de venda atual

```
1. Operador carrega login → Abrir caixa
2. Digita produto/código/barras no campo principal → Enter
3. Se resultado único → auto-seleciona e adiciona ao carrinho
4. Se múltiplos resultados → duplo clique no grid de busca
5. (opcional) Troca quantidade e desconto → Enter novamente
6. Repete para cada item
7. Seleciona espécie e condição de pagamento
8. Digita valor recebido → clica "Adicionar"
9. Repete para cada forma de pagamento
10. Clica "Finalizar venda"
```

---

## Problemas identificados

### P1 — Enter não fecha o pagamento (impacto alto / esforço baixo)

**Arquivo:** `MainWindow.xaml.cs` — `AddPaymentButton_Click` (linha 356)

O campo `PaymentReceivedTextBox` não tem `KeyDown`. Após digitar o valor
recebido, o operador precisa mover a mão para o mouse e clicar "Adicionar".
O mesmo ocorre no campo de login do supervisor: digita login, precisa clicar
"Autorizar".

**Correção:** adicionar `KeyDown` em `PaymentReceivedTextBox` e em
`SupervisorLoginTextBox` que interceptem `Key.Enter` e disparem a ação
correspondente.

---

### P2 — Campo "Valor recebido" inicia zerado (impacto alto / esforço baixo)

**Arquivo:** `MainWindow.xaml.cs` — `ResetSale` (linha 969)

Ao chegar na etapa de pagamento, o campo `PaymentReceivedTextBox` exibe
`0,00`. O operador precisa apagar e digitar o valor total ou o troco. O
método `CalculateRemainingAmount()` já existe mas não é usado para pré-preencher.

**Correção:** no evento `GotFocus` do `PaymentReceivedTextBox`, preencher
com `FormatDecimal(CalculateRemainingAmount())` se o valor atual for zero.

---

### P3 — Foco não vai ao campo de quantidade após selecionar produto (impacto alto / esforço baixo)

**Arquivo:** `MainWindow.xaml.cs` — `SelectProduct` (linha 602)

Após duplo clique no grid de resultados de busca, o foco retorna ao campo
de produto. Se o operador precisa alterar a quantidade (vendas pesadas, por
exemplo), precisa clicar manualmente em `QuantityTextBox`.

**Correção:** em `SelectProduct`, substituir `FocusProductEntry` por
`QuantityTextBox.SelectAll(); QuantityTextBox.Focus();`. O operador digita
a quantidade e pressiona Enter para adicionar ao carrinho.

---

### P4 — Nenhum atalho de teclado para ações principais (impacto alto / esforço baixo)

**Arquivo:** `MainWindow.xaml` — nenhum `KeyBinding` definido

O único atalho implementado é `Enter` no campo de produto. Todas as outras
ações dependem do mouse.

**Atalhos sugeridos:**

| Tecla | Ação |
|-------|------|
| `F12` | Finalizar venda |
| `F2` | Nova venda |
| `Esc` | Limpar seleção de produto / cancelar busca |
| `Enter` em `PaymentReceivedTextBox` | Adicionar pagamento |
| `Enter` em `SupervisorLoginTextBox` | Autorizar supervisor |

**Implementação:** `Window.KeyDown` global ou `InputBindings` no XAML.

---

### P5 — Grid de busca com altura fixa pequena (impacto médio / esforço baixo)

**Arquivo:** `MainWindow.xaml` — linha 277 (`Height="118"`)

Com 118px o grid mostra 4–5 linhas. Com o limite de 20 resultados retornados
pelo repositório, o operador precisa rolar para ver os demais.

**Correção:**

```xml
<!-- antes -->
<DataGrid x:Name="ProductSearchResultsGrid" Height="118" ...>

<!-- depois -->
<DataGrid x:Name="ProductSearchResultsGrid" MaxHeight="180" ...>
```

O grid colapsa quando vazio e cresce até 180px conforme os resultados.

---

### P6 — Scroll automático para o último item ausente (impacto médio / esforço baixo)

**Arquivo:** `MainWindow.xaml.cs` — `AddSelectedProductToCart` (linha 637)

Com carrinho longo, o último item adicionado fica fora da view sem o
operador perceber.

**Correção:** após `_saleItems.Add(...)`:

```csharp
CartItemsGrid.ScrollIntoView(CartItemsGrid.Items[^1]);
```

---

### P7 — Indicador de "venda em andamento" ausente (impacto médio / esforço baixo)

O cabeçalho da seção diz apenas "Venda". Quando há itens no carrinho não
há destaque visual. Em ambiente de alta rotatividade, o operador pode
iniciar nova venda por engano.

**Correção:** atualizar `SaleGateValue` ou o título "Venda" para exibir
dinamicamente "Venda — 3 itens — R$ 47,80" ao final de cada
`UpdateTotals()`.

---

### P8 — Painel técnico de conectividade ocupa espaço da coluna de caixa (impacto alto / esforço médio)

**Arquivo:** `MainWindow.xaml` — linhas 524–591 (dois `Border` com heartbeat,
pgvector, schema PDV, ERP API, banco, erros)

São 12 campos de diagnóstico técnico que o operador de caixa nunca usa
durante a venda. Eles empurram os painéis Caixa, Autorização e Pagamento
para baixo da área visível, forçando scroll na coluna direita.

**Correção:** colapsar por padrão. Adicionar um `Expander` com texto
"Detalhes técnicos" que só expande quando necessário (diagnóstico de
problemas pelo supervisor ou TI).

---

### P9 — Cards de monitoramento comprimem a área de venda (impacto médio / esforço médio)

**Arquivo:** `MainWindow.xaml` — linhas 136–171 (`UniformGrid Columns="5"`)

Os cinco cards (Ativação, Runtime, Pendentes, Dead-letter, Caixas abertos)
ficam no topo da coluna de venda, reduzindo a altura disponível para o
carrinho e os campos de produto.

**Correção:** mover os cards para dentro do painel direito (abaixo do
resumo do caixa) ou incorporá-los no banner de status já existente.

---

### P10 — Autorização de supervisor é limpa a cada nova venda (impacto alto / esforço médio)

**Arquivo:** `MainWindow.xaml.cs` — `ResetSale` → `ClearSupervisorAuthorization` (linha 965)

Em vendas com desconto recorrente (promoção do dia, por exemplo), o
supervisor precisa reautorizar a cada nova venda: digitar login → digitar
motivo → clicar "Autorizar". São três passos extras por venda.

**Correção:** separar dois escopos de autorização:

- **Autorização por operação** (comportamento atual): necessária para
  remover item ou pagamento individualmente.
- **Autorização de sessão** (novo): supervisor faz login uma vez no
  início do turno e a autorização persiste para descontos durante toda a
  sessão do caixa. Limpa apenas no fechamento de caixa ou por ação
  explícita do supervisor.

---

### P11 — Grid de pagamentos com altura fixa (impacto baixo / esforço baixo)

**Arquivo:** `MainWindow.xaml` — linha 485 (`Height="125"`)

Em vendas com múltiplas formas de pagamento o grid fica apertado com 125px
(3–4 linhas visíveis).

**Correção:**

```xml
<!-- antes -->
<DataGrid x:Name="PaymentsGrid" Height="125" ...>

<!-- depois -->
<DataGrid x:Name="PaymentsGrid" MaxHeight="160" ...>
```

---

### P12 — Desconto total sem indicação visual quando ativo (impacto baixo / esforço baixo)

**Arquivo:** `MainWindow.xaml` — `SaleDiscountTextBox` (linha 325)

O campo `Desc. total` fica na mesma linha que Subtotal e Total, sem
destaque. Quando o desconto está ativo, o operador pode não perceber.

**Correção:** no handler `SaleDiscountTextBox_TextChanged`, mudar a cor do
`BorderBrush` do campo para laranja/âmbar quando o valor for maior que zero.

---

## Tabela de prioridades

| # | Melhoria | Impacto | Esforço | Status |
|---|----------|---------|---------|--------|
| P1 | Enter para adicionar pagamento e autorizar supervisor | Alto | Baixo | ✓ Implementado |
| P2 | Auto-preencher "Valor recebido" com restante | Alto | Baixo | ✓ Implementado |
| P3 | Foco em `QuantityTextBox` após selecionar produto | Alto | Baixo | ✓ Implementado |
| P4 | Atalhos de teclado F2/F12/Esc | Alto | Baixo | ✓ Implementado |
| P5 | Grid de busca com altura dinâmica | Médio | Baixo | ✓ Implementado |
| P6 | Scroll automático no carrinho | Médio | Baixo | ✓ Implementado |
| P7 | Indicador de venda em andamento no cabeçalho | Médio | Baixo | ✓ Implementado |
| P11 | Grid de pagamentos com altura dinâmica | Baixo | Baixo | ✓ Implementado |
| P12 | Destaque visual no campo de desconto ativo | Baixo | Baixo | ✓ Implementado |
| P8 | Colapsar painel técnico na coluna direita | Alto | Médio | ✓ Implementado |
| P9 | Mover cards de monitoramento para fora da coluna de venda | Médio | Médio | ✓ Implementado |
| P10 | Autorização de supervisor com escopo de sessão | Alto | Médio | ✓ Implementado |

---

## Comportamento atual vs. esperado — fluxo típico de venda com desconto

### Atual (com fricção)

```
1. Supervisor digita login → digita motivo → clica "Autorizar"        [3 ações]
2. Operador digita produto → Enter                                     [2 ações]
3. Operador digita desconto do item                                    [1 ação]
4. Operador clica "Adicionar" (não tem Enter aqui)                     [1 ação]
5. Repete para cada item
6. Operador vai ao campo de pagamento, apaga 0,00, digita valor        [3 ações]
7. Operador clica "Adicionar"                                          [1 ação]
8. Operador clica "Finalizar venda"                                    [1 ação]
9. Na próxima venda com desconto: supervisor reautoriza                [3 ações]
```

### Pós-melhorias (P1–P4 + P10)

```
1. Supervisor loga uma vez no início do turno                          [1 ação]
2. Operador digita produto → Enter (adiciona ao carrinho direto)       [2 ações]
3. Para desconto: informa valor, Enter para adicionar                  [2 ações]
4. Tab vai para pagamento, valor já preenchido → Enter para adicionar  [1 ação]
5. F12 para finalizar                                                  [1 ação]
6. Próxima venda com desconto: supervisor já autorizado                [0 ações]
```

As melhorias de baixo esforço (P1–P7, P11, P12) eliminam aproximadamente
**5–8 cliques de mouse por venda**, o que em um caixa de 200 vendas/dia
representa ~1.000–1.600 cliques a menos por turno.
