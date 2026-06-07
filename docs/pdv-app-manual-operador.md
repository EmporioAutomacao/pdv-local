# PDV Local — Manual do operador

## Visão geral

O PDV Local é a frente de caixa offline-first usada pelo operador para registrar
vendas, receber pagamentos e controlar o caixa. Ele funciona mesmo sem conexão com
a internet; as vendas são sincronizadas com o ERP automaticamente quando a conexão
é restabelecida.

---

## Início de turno

### 1. Carregar o operador

1. No painel **Caixa** (coluna direita), digite o login no campo **Login do operador**.
2. Pressione **Enter** ou clique **Carregar**.
3. O campo **Operador atual** exibe o nome e o papel do operador confirmado.

> Se o login não for encontrado, o campo mostra erro em vermelho. Verifique o login
> ou solicite ao administrador que sincronize os operadores do ERP.

### 2. Abrir o caixa

1. Informe o **Valor de abertura** (dinheiro em cédulas no cofre no início do turno).
2. Pressione **Enter** ou clique **Abrir caixa**.
3. O status da venda muda para **verde**: "Caixa aberto. Lance produtos pelo campo principal."

> Se já existe um caixa aberto para esse operador, ele é reaproveitado automaticamente.

### 3. Autorizar o supervisor (opcional, necessário para descontos)

Se o turno terá vendas com desconto, o supervisor pode autorizar uma única vez no
início do turno:

1. No painel **Autorização**, digite o login do supervisor no campo **Login supervisor/admin**.
2. Pressione **Enter** ou clique **Autorizar**.
3. O campo **Autorização atual** fica **verde** com o nome do supervisor.

A autorização persiste durante toda a sessão do caixa. Para cada desconto, o
operador informa apenas o **motivo** (campo Motivo), sem precisar redigitar o login.

---

## Registrar uma venda

### Fluxo padrão (código de barras ou SKU)

1. Aponte o leitor de código de barras ao produto — o código preenche o campo
   **Produto** automaticamente.
2. O produto é localizado e adicionado ao carrinho em **um único evento de leitura**.
3. O campo **Produto** se limpa e aguarda o próximo item.

### Fluxo por nome

1. Digite parte do nome do produto no campo **Produto** e pressione **Enter** ou
   clique **Buscar**.
2. Se houver **um único resultado**, o produto é selecionado automaticamente.
3. Se houver **múltiplos resultados**, o grid de busca exibe as opções:
   - Use **↑** e **↓** para navegar entre os resultados.
   - Pressione **Enter** ou dê **duplo clique** para selecionar.
4. O foco vai para o campo **Qtd** com o valor "1" selecionado.
   - Para alterar a quantidade, basta digitar o novo valor.
   - Pressione **Enter** no campo Qtd para adicionar ao carrinho.

### Adicionar com desconto no item

1. Selecione o produto (campo Produto + Enter).
2. No campo **Desc. item**, informe o valor do desconto em reais.
3. Preencha o **Motivo** no painel Autorização (o supervisor deve estar autorizado).
4. Pressione **Enter** no campo Desc. item ou clique **Adicionar**.

> O campo **Desc. item** fica com fundo âmbar quando há desconto ativo.

### Aplicar desconto total na venda

1. Após adicionar todos os itens, informe o valor no campo **Desc. total**
   (abaixo do subtotal).
2. O total é atualizado em tempo real.
3. Na finalização, o sistema solicitará o motivo do desconto (supervisor deve
   estar autorizado).

### Remover um item do carrinho

- Selecione o item no carrinho e pressione **Delete**, ou clique **Remover item**.
- Requer motivo e autorização de supervisor.

### Cancelar a venda em andamento

- Pressione **F2** ou clique **Nova venda**.
- Se houver itens ou pagamentos, requer motivo e autorização de supervisor.

### Rascunho automático

O sistema salva automaticamente um rascunho da venda em andamento após cada item adicionado ou removido. Se o aplicativo for fechado ou reiniciado com itens no carrinho, ao reabrir o caixa o rascunho é restaurado automaticamente com todos os itens e o desconto total que estavam preenchidos.

> O rascunho é apagado quando a venda é finalizada, cancelada, ou quando o operador clica "Nova venda".

---

## Registrar o pagamento

O painel **Pagamento** fica na coluna direita. Quando o foco entra no campo
**Valor recebido**, ele é preenchido automaticamente com o valor restante a pagar.

### Pagamento simples (uma forma)

1. Selecione a **Espécie** (ex.: dinheiro, Pix, cartão débito).
2. Selecione a **Condição** (ex.: à vista).
3. O campo **Valor recebido** já contém o total — confirme ou ajuste para o valor
   entregue pelo cliente.
4. Pressione **Enter** ou clique **Adicionar**.
5. O troco é exibido em verde no campo **Troco**.

### Pagamento misto (mais de uma forma)

1. Adicione o primeiro pagamento parcial (ex.: R$ 50,00 em dinheiro).
2. O campo **Valor recebido** atualiza automaticamente com o restante.
3. Selecione a segunda espécie e adicione o segundo pagamento.
4. Repita até o campo **Restante** zerar.

> O campo **Restante** fica em **vermelho** enquanto houver valor a pagar e volta ao
> preto quando a venda está totalmente paga.

### Remover um pagamento

- Selecione o pagamento na grade e pressione **Delete**, ou clique **Remover**.
- Requer motivo e autorização de supervisor.

### Finalizar a venda

- Pressione **F12** ou clique **Finalizar venda**.
- A venda é gravada localmente e marcada para sincronização com o ERP.
- A janela de **Comprovante de Venda** abre automaticamente.
- O carrinho é limpo e o campo **Produto** recebe foco para a próxima venda.

### Comprovante de venda

A janela do comprovante abre automaticamente após cada finalização. Ela exibe:

- Número da venda, data/hora e operador
- Todos os itens com quantidade, preço unitário e total
- Descontos por item e total (quando aplicados)
- Formas de pagamento e valores
- Troco

**Imprimir:** clique **Imprimir** para enviar para qualquer impressora configurada
no Windows (incluindo impressoras térmicas de 80mm).

**Fechar:** clique **Fechar** ou pressione **Esc**. A janela é não-bloqueante — a
próxima venda pode ser iniciada enquanto o comprovante ainda está aberto.

### Reimprimir o último comprovante

Após a primeira venda finalizada na sessão, o botão **Reimprimir** aparece no
cabeçalho do sistema (ao lado de "Vendas do caixa"). Clicar nele reabre o
comprovante da última venda concluída.

> O botão só fica visível depois da primeira venda. Ele é redefinido ao fechar e
> reabrir o aplicativo.

### Consultar vendas do caixa

O botão **Vendas do caixa** no cabeçalho (habilitado quando há caixa aberto)
abre a janela de consulta de vendas da sessão atual. A janela exibe:

| Coluna | Conteúdo |
|--------|----------|
| Número | Código único da venda (`PDV-yyyyMMddHHmmss`) |
| Hora | Horário de finalização (fuso local) |
| Itens | Quantidade de linhas de produto |
| Total | Valor total da venda |
| Pagamentos | Formas de pagamento usadas |
| Sync | Status de sincronização com o ERP |

O status de sincronização exibe:

| Status | Significado |
|--------|-------------|
| Sincronizado | Enviado ao ERP com sucesso |
| Pendente | Aguardando próxima janela de sync |
| Erro | Falha na sincronização — verificar SyncAgent |

O rodapé da janela mostra o total de vendas finalizadas e a soma dos valores da sessão (excluindo canceladas). Vendas canceladas aparecem em itálico cinza.

### Cancelar uma venda finalizada

1. Em **Vendas do caixa** (F8), clique em uma venda finalizada para selecioná-la.
2. O painel de cancelamento abre abaixo da lista.
3. Preencha o **login do supervisor** e o **motivo** do cancelamento.
4. Pressione **Enter** no campo Motivo ou clique **Confirmar cancelamento**.

O sistema valida que o supervisor está ativo e tem papel supervisor/admin. A venda é marcada como cancelada no banco e fica na fila de sincronização para o ERP ser notificado. O painel fecha e a lista é atualizada automaticamente.

> O cancelamento é irreversível no PDV Local. Para anulação fiscal, consulte o fluxo do ERP.

---

## Movimentos de caixa

### Suprimento (entrada de dinheiro)

1. Informe o valor no campo **Movimento de caixa**.
2. Preencha o Motivo no painel Autorização.
3. Clique **Suprimento**.

### Sangria (retirada de dinheiro)

1. Informe o valor no campo **Movimento de caixa**.
2. Preencha o Motivo no painel Autorização.
3. Clique **Sangria**.

> A sangria não pode ultrapassar o dinheiro esperado em caixa.

---

## Encerramento de turno

### Fechar o caixa

1. Certifique-se de que não há venda em andamento (carrinho vazio).
2. Conte o dinheiro físico e informe o valor no campo **Fechamento**.
3. Pressione **Enter** ou clique **Fechar caixa**.
4. O sistema exibe o resumo e calcula a diferença entre o valor informado e o
   esperado.

---

## Monitoramento do sistema

O **banner** no topo da tela indica o estado da conexão:

| Cor | Significado |
|-----|-------------|
| Verde | Conectado ao ERP, sem pendências |
| Azul | Conectado, há vendas aguardando sincronização |
| Amarelo | Problema de heartbeat ou pendências |
| Vermelho | Sem conexão com o SyncAgent |

O status é atualizado automaticamente a cada 60 segundos. Para atualização
imediata, clique **Atualizar**.

Para ver detalhes técnicos (heartbeat, banco, erros), clique em
**Detalhes tecnicos** no rodapé da coluna direita.

---

## Referência de atalhos de teclado

| Tecla | Campo / Contexto | Ação |
|-------|-----------------|------|
| `Enter` | Login do operador | Carregar operador |
| `Enter` | Valor de abertura | Abrir caixa |
| `Enter` | Valor de fechamento | Fechar caixa |
| `Enter` | Campo de produto | Buscar produto / adicionar ao carrinho |
| `↑` `↓` | Grid de resultados | Navegar entre produtos encontrados |
| `Enter` | Grid de resultados | Selecionar produto destacado |
| `Enter` | Quantidade | Adicionar item ao carrinho |
| `Enter` | Desconto do item | Adicionar item ao carrinho |
| `Delete` | Item selecionado no carrinho | Remover item (requer autorização) |
| `Enter` | Valor recebido | Adicionar pagamento |
| `Delete` | Pagamento selecionado | Remover pagamento (requer autorização) |
| `Enter` | Login supervisor | Autorizar supervisor |
| `F2` | Qualquer tela | Nova venda |
| `F8` | Qualquer tela (caixa aberto) | Abrir consulta de vendas do caixa |
| `F9` | Qualquer tela (após 1ª venda) | Reimprimir último comprovante |
| `F12` | Qualquer tela | Finalizar venda |
| `Esc` | Produto selecionado | Cancelar seleção de produto |

---

## Dicas de operação rápida

- **Leitor de código de barras**: aponte para o produto e o item vai direto para o
  carrinho. Sem nenhuma tecla adicional.
- **Supervisor no início do turno**: autorize uma vez e o sistema não pedirá o login
  do supervisor de novo até a troca de operador.
- **Pagamento em dinheiro**: o valor recebido já vem preenchido com o total —
  basta conferir e pressionar Enter.
- **Desconto no item antes de adicionar**: selecione o produto, tab para Desc. item,
  informe o valor, Enter.
- **Tudo pelo teclado**: do login ao fechamento de caixa, nenhuma ação obriga o uso
  do mouse.
