# PDV Local — Manual do operador

## Visão geral

O PDV Local é a frente de caixa offline-first usada pelo operador para registrar
vendas, receber pagamentos e controlar o caixa. Ele funciona mesmo sem conexão com
a internet; as vendas são sincronizadas com o ERP automaticamente quando a conexão
é restabelecida.

A tela principal é dedicada à **venda**. As demais funções (caixa, pagamento,
autorização, consulta de preço, diagnóstico) abrem em janelas próprias, chamadas
por teclas de função — como nos PDVs de mercado.

---

## Início de turno

### 1. Login

Ao abrir o PDV, a primeira tela é o **login**:

1. Digite o **usuário** e a **senha** (os mesmos do ERP, sincronizados para o PDV).
2. Pressione **Enter** ou clique **Entrar**.

> Se o login falhar, verifique usuário/senha ou solicite ao administrador que
> sincronize os operadores do ERP.

### 2. Abrir o caixa

Se o operador não tem caixa aberto, o diálogo **Caixa** abre automaticamente
após o login:

1. Informe o **Fundo de troco (abertura)** — dinheiro em cédulas no início do turno.
2. Pressione **Enter** ou clique **Abrir caixa**.
3. O diálogo fecha e a tela de venda é liberada.

Se o operador já tinha caixa aberto (ex.: reinício do aplicativo), a venda é
liberada direto, sem diálogo.

> Para abrir o diálogo do caixa a qualquer momento, pressione **F4**.
> A barra de status inferior mostra sempre a situação: "Caixa: aberto desde ..."
> ou "Caixa: fechado — F4 para abrir".

---

## Registrar uma venda

### Fluxo padrão (código de barras ou SKU)

1. Aponte o leitor de código de barras ao produto — o código preenche o campo
   **Produto** automaticamente.
2. O produto é localizado e adicionado ao carrinho em **um único evento de leitura**.
3. O campo **Produto** se limpa e aguarda o próximo item.

### Quantidade rápida (quantidade*código)

Digite a quantidade, um asterisco e o código no campo **Produto**:

```
3*1187      →  3 unidades do código 1187
1,5*7891234 →  1,5 kg/un do código de barras 7891234
```

### Fluxo por nome

1. Digite parte do nome do produto no campo **Produto** e pressione **Enter** ou
   clique **Buscar**.
2. Se houver **um único resultado**, o produto é selecionado automaticamente.
3. Se o código digitado casar **exatamente** com o ID, código de barras, SKU
   ou código de fábrica de um único produto, ele é lançado direto — mesmo que
   o texto também apareça no nome de outros produtos.
4. Se houver **múltiplos resultados** sem match exato, o grid de busca exibe
   as opções: use **↑** e **↓** para navegar; **Enter** ou **duplo clique**
   para selecionar.
5. O foco vai para o campo **Qtd** — ajuste se necessário e pressione **Enter**.

### Produto com mais de uma unidade de venda

Quando o produto tem mais de uma unidade cadastrada no ERP (ex.: **UN** e
**CX** — caixa fechada com 6, 12...), ao selecioná-lo abre o diálogo
**Unidade de venda**:

1. As opções aparecem numeradas, com a descrição e o preço de cada unidade.
2. Escolha pelo **número** (1, 2, 3...) ou por **setas + Enter**; duplo clique
   também seleciona.
3. O item entra no carrinho com a unidade escolhida (coluna **Un**) e o
   preço correspondente.

> Produtos com uma única unidade seguem lançando direto, sem diálogo.

### Consulta de preço (F3)

Pressione **F3** para consultar o preço de um produto **sem lançar** na venda.
Busque por código, código de barras ou nome; o preço aparece em destaque.
Se o produto tiver mais de uma unidade, os preços de cada uma aparecem
abaixo do preço principal. Feche com **Esc**.

### Adicionar com desconto no item

1. Selecione o produto (campo Produto + Enter).
2. No campo **Desc. item**, informe o valor do desconto em reais.
3. Pressione **Enter** — o diálogo **Autorização de supervisor** abre: o
   supervisor informa **login, senha e motivo**.

> Cada operação sensível (desconto, remoção, cancelamento, sangria) pede a sua
> própria autorização — não existe mais autorização "por turno".

### Remover um item do carrinho

- Selecione o item no carrinho e pressione **Delete**, ou clique **Remover item**.
- Abre o diálogo de autorização de supervisor (login + senha + motivo).

### Cancelar a venda em andamento

- Pressione **F2** ou clique **Nova venda**.
- Se houver itens ou pagamentos, abre o diálogo de autorização de supervisor.

### Rascunho automático

O sistema salva automaticamente um rascunho da venda em andamento após cada item
adicionado ou removido. Se o aplicativo for fechado ou reiniciado com itens no
carrinho, ao fazer login novamente o rascunho é restaurado com todos os itens e o
desconto que estavam preenchidos.

> O rascunho é apagado quando a venda é finalizada, cancelada, ou quando o
> operador inicia uma Nova venda.

---

## Pagamento e finalização (F10)

Com os itens no carrinho, pressione **F10** (ou **F12**, ou clique
**Pagamento (F10)**) para abrir a janela de **Pagamento**:

1. **Total da venda** em destaque; o **Desconto total** pode ser informado aqui
   (na conclusão, o sistema pedirá autorização de supervisor).
2. **Cliente** (opcional para venda à vista): digite o **CPF/CNPJ** ou o
   **código interno** do cliente no ERP e pressione **Enter** — ou clique na
   **lupa** para pesquisar por nome, documento ou código. Se o cliente
   estiver cadastrado, o nome aparece ao lado do campo; o documento é
   gravado na venda e enviado ao ERP.
3. Selecione a **Espécie** (dinheiro, Pix, cartão...) e a **Condição**.
   - A condição padrão é **À vista**.
   - Se a condição tiver **mais de 1 parcela** ou **primeiro vencimento
     futuro**, o pagamento **exige cliente cadastrado** — não basta digitar
     um CPF/CNPJ avulso; é preciso que o cliente já exista no ERP.
4. O campo **Valor recebido** é preenchido automaticamente com o restante —
   confirme ou ajuste, e pressione **Enter** (ou clique **Adicionar**).
5. Para **pagamento misto**, repita com outras espécies até o **Restante** zerar.
6. O **Troco** aparece em verde.
7. Clique **Concluir venda (F10)** — só habilita com restante zero.

- **Esc** volta à tela de venda **preservando** os pagamentos já lançados.
- Para remover um pagamento: selecione na grade e **Delete** (requer supervisor).

### Parcelas (pagamento a prazo)

Ao adicionar um pagamento numa condição parcelada, o sistema calcula as
datas de vencimento automaticamente (a partir da condição de pagamento
cadastrada no ERP) e mostra a grade de **parcelas** abaixo da lista de
pagamentos, com o pagamento selecionado:

1. Cada linha mostra o **número**, o **vencimento** e o **valor** da parcela.
2. O **vencimento é editável**: clique na célula e digite a nova data no
   formato `dd/mm/aaaa`.
3. Ao **concluir a venda**, o sistema valida que a soma das parcelas bate com
   o valor do pagamento e que as datas não retrocedem.

> As parcelas são enviadas ao ERP junto com a venda e viram títulos
> financeiros (parcelas a receber) automaticamente.

Após concluir:

- A venda é gravada localmente e marcada para sincronização com o ERP.
- A janela de **Comprovante de Venda** abre automaticamente.
- O carrinho é limpo e o campo **Produto** recebe foco para a próxima venda.

### Comprovante de venda

A janela do comprovante exibe número da venda, data/hora, operador, itens,
descontos, formas de pagamento e troco.

**Imprimir:** envia para qualquer impressora configurada no Windows (incluindo
térmicas de 80mm). **Fechar:** botão Fechar ou **Esc** — a janela é
não-bloqueante.

### Reimprimir o último comprovante (F9)

Após a primeira venda finalizada na sessão, o botão **F9 Reimprimir** aparece no
cabeçalho e a tecla **F9** reabre o comprovante da última venda.

### Consultar vendas do caixa (F8)

O botão **F8 Vendas** (habilitado com caixa aberto) abre a consulta de vendas da
sessão, com status de sincronização por venda (Sincronizado, Enviado, Pendente,
Rejeitado, Cancelado) e totais no rodapé.

### Detalhe da venda, reimpressão e cancelamento

Clicar em qualquer venda da lista (F8) abre a tela de **Detalhe da venda**:

- **Itens**: produto, unidade, quantidade, preço e desconto de cada linha.
- **Pagamentos**: espécie, valor e, se houver, as **parcelas** (datas e valores).
- **Reimprimir**: monta o comprovante a partir do banco e abre a janela de
  impressão — funciona para qualquer venda da sessão, não só a última.
  > O troco não fica gravado no banco; a reimprimissão de vendas antigas não
  > mostra a linha de troco.
- **Cancelar venda** (só aparece em vendas finalizadas): pede **login, senha
  e motivo** do supervisor, igual às demais operações sensíveis.

> O cancelamento é irreversível no PDV Local. Para anulação fiscal, consulte o
> fluxo do ERP.

---

## Caixa (F4): movimentos e fechamento

Pressione **F4** para abrir o diálogo **Caixa**. Com o caixa aberto ele mostra o
**resumo do turno** em uma lista (abertura, vendas, suprimentos, sangrias) —
o dinheiro esperado e as vendas por espécie **não aparecem aqui**: eles só são
revelados no relatório, depois do fechamento (ver abaixo).

### Suprimento (entrada de dinheiro)

1. Informe o valor no campo **Movimento de caixa**.
2. Preencha a **Observação** (obrigatória — ex.: "reforço de troco").
3. Clique **Suprimento** — abre a autorização de supervisor (login + senha + motivo).

### Sangria (retirada de dinheiro)

1. Informe o valor no campo **Movimento de caixa**.
2. Preencha a **Observação** (obrigatória — ex.: "retirada para depósito").
3. Clique **Sangria** — abre a autorização de supervisor.

> A sangria não pode ultrapassar o dinheiro esperado em caixa.
> A observação fica gravada no histórico do movimento; o motivo dado ao
> supervisor fica na auditoria.

### Fechar o caixa (contagem cega por espécie)

O fechamento é **cego**: o operador informa o valor contado de cada espécie
**sem ver o esperado** — a diferença só aparece depois, no relatório.

1. Certifique-se de que não há venda em andamento (carrinho vazio).
2. No diálogo Caixa (F4), preencha a grade **Fechamento** com o valor contado
   de cada espécie (Dinheiro, Pix, Cartão...). Clique na célula **Contado**
   para editar.
3. Clique **Fechar caixa**.
4. Abre automaticamente o **relatório de fechamento**, mostrando esperado,
   contado e diferença por espécie, os suprimentos/sangrias com observação, e
   a diferença total.
   - Escolha o formato **A4** ou **Térmica (72mm)** e clique **Imprimir**.
   - Feche com **Esc** ou o botão **Fechar**.

---

## Troca de operador (F11) e travamento (Ctrl+L)

- **F11 — Trocar operador**: abre a tela de login para outro operador assumir o
  terminal. Bloqueado com venda em andamento. O caixa do operador anterior
  permanece aberto (cada operador tem o seu).
- **Ctrl+L — Travar terminal**: bloqueia a tela ao se ausentar do caixa. Para
  voltar, digite a senha do operador logado.

---

## Configurações e diagnóstico

O botão **Configurações** abre as preferências:

### Tema

| Opção | Descrição |
|-------|-----------|
| **Claro** (padrão) | Fundo branco, texto escuro |
| **Escuro** | Fundo cinza-chumbo, texto claro — reduz fadiga em pouca luz |

A preferência é salva e aplicada na próxima abertura. A troca é instantânea.

### Diagnóstico e ativação (Ctrl+D)

Em **Configurações > Diagnóstico e ativação** (ou **Ctrl+D**) abre a janela
técnica com: status do SyncAgent (heartbeat, pendências, dead-letter), banco
local (pgvector, schema, contagens), ativação da instalação e o botão
**Verificar atualização**.

---

## Barra de status e banner

A **barra de status** na parte inferior mostra, em tempo integral:

```
Operador: Nome (login)  |  Caixa: aberto desde 20/07 08:02  |  Sync: OK  |  Versao 1.2.0  |  20/07/2026 14:32:05
```

O **banner** no topo só aparece quando algo precisa de atenção:

| Cor | Significado |
|-----|-------------|
| (oculto) | Tudo certo — conectado e sem pendências |
| Amarelo | Instalação não ativada ou problema de heartbeat |
| Vermelho | Sem conexão com o SyncAgent |

---

## Referência de atalhos de teclado

| Tecla | Contexto | Ação |
|-------|----------|------|
| `F1` | Tela de venda | Abrir ajuda integrada |
| `F2` | Tela de venda | Nova venda (autorização se houver itens) |
| `F3` | Tela de venda | Consulta de preço |
| `F4` | Tela de venda | Caixa (abrir/fechar/suprimento/sangria) |
| `F8` | Caixa aberto | Vendas do caixa |
| `F9` | Após 1ª venda | Reimprimir último comprovante |
| `F10` / `F12` | Tela de venda | Pagamento / finalizar venda |
| `F11` | Tela de venda | Trocar operador |
| `Ctrl+D` | Tela de venda | Diagnóstico e ativação |
| `Ctrl+L` | Tela de venda | Travar terminal |
| `qtd*código` | Campo Produto | Lançar quantidade de uma vez (ex.: `3*1187`) |
| `Enter` | Campo Produto | Buscar produto / adicionar ao carrinho |
| `↑` `↓` | Grid de resultados | Navegar entre produtos |
| `1`-`9` / `↑↓`+`Enter` | Diálogo Unidade de venda | Selecionar a unidade (produto com mais de uma) |
| `Enter` | Quantidade / Desc. item | Adicionar item ao carrinho |
| `Delete` | Item no carrinho | Remover item (autorização) |
| `Enter` | Campo Cliente (Pagamento) | Resolver CPF/CNPJ ou código interno |
| `Enter` | Valor recebido (Pagamento) | Adicionar pagamento |
| `Delete` | Pagamento na grade | Remover pagamento (autorização) |
| `Esc` | Diálogos | Fechar / voltar preservando o estado |
| `Esc` | Produto selecionado | Cancelar seleção de produto |

---

## Dicas de operação rápida

- **Leitor de código de barras**: aponte para o produto e o item vai direto para o
  carrinho, sem nenhuma tecla adicional.
- **Quantidade rápida**: `3*código` lança 3 unidades de uma vez.
- **Pagamento em dinheiro**: F10 → o valor recebido já vem preenchido — Enter,
  Concluir, pronto.
- **CPF na nota**: digite no campo próprio da janela de Pagamento; o sistema
  valida e reconhece clientes cadastrados.
- **Não lembra o CPF do cliente?** Clique na lupa ao lado do campo e busque por
  nome ou código interno.
- **Venda a prazo**: garanta que o cliente está cadastrado *antes* de escolher
  a condição parcelada — senão o pagamento é recusado.
- **Reimprimir venda antiga**: F8 → clique na venda → Reimprimir (não precisa
  ser a última venda do turno).
- **Ausentou-se do caixa?** Ctrl+L trava o terminal na hora.
- **Tudo pelo teclado**: do login ao fechamento de caixa, nenhuma ação obriga o
  uso do mouse.
- **Dúvida rápida?** Pressione **F1**.
