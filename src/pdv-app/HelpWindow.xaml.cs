using System.Windows;

namespace PdvLocal.App;

public partial class HelpWindow : Window
{
    private static readonly List<HelpRow> GlobalShortcuts =
    [
        new("F1",    "Qualquer tela",           "Abrir esta janela de ajuda"),
        new("F2",    "Tela de venda",           "Nova venda (exige supervisor se houver itens)"),
        new("F3",    "Tela de venda",           "Consulta de preço (não lança item na venda)"),
        new("F4",    "Tela de venda",           "Abrir diálogo do caixa (abrir/fechar/suprimento/sangria)"),
        new("F8",    "Caixa aberto",            "Abrir consulta de vendas do caixa"),
        new("F9",    "Após 1ª venda",           "Reimprimir último comprovante de venda"),
        new("F10",   "Tela de venda",           "Pagamento / finalizar venda (F12 também funciona)"),
        new("F11",   "Tela de venda",           "Trocar de operador (bloqueado com venda em andamento)"),
        new("Ctrl+D","Tela de venda",           "Diagnóstico e ativação"),
        new("Ctrl+L","Tela de venda",           "Travar terminal (exige a senha do operador para voltar)"),
        new("Esc",   "Produto selecionado",     "Cancelar seleção de produto e limpar campos"),
        new("Esc",   "Diálogos",                "Fechar o diálogo atual"),
    ];

    private static readonly List<HelpRow> FieldShortcuts =
    [
        new("Enter", "Tela de login",           "Entrar"),
        new("Enter", "Fundo de troco (Caixa)",  "Abrir caixa"),
        new("Enter", "Valor contado (Caixa)",   "Fechar caixa"),
        new("Enter", "Campo Produto",           "Buscar produto / adicionar ao carrinho"),
        new("qtd*código", "Campo Produto",      "Ex.: 3*1187 lança 3 unidades do código 1187"),
        new("↑  ↓",  "Grid de resultados",     "Navegar entre produtos encontrados"),
        new("Enter", "Grid de resultados",      "Selecionar produto destacado"),
        new("Enter", "Quantidade",              "Adicionar item ao carrinho"),
        new("Enter", "Desconto do item",        "Adicionar item ao carrinho"),
        new("Delete","Item no carrinho",        "Remover item (exige supervisor e motivo)"),
        new("Enter", "Valor recebido (Pagamento)","Adicionar pagamento"),
        new("Delete","Pagamento na grade",      "Remover pagamento (exige supervisor e motivo)"),
        new("Enter", "Diálogo de autorização",  "Confirmar autorização do supervisor (login + senha)"),
        new("Enter", "Login supervisor (cancel.)","Avançar para campo Motivo"),
        new("Enter", "Motivo (cancelamento)",   "Confirmar cancelamento de venda"),
    ];

    private static readonly List<HelpRow> VisualCues =
    [
        new("Restante (Pagamento)",  "Vermelho → valor ainda a pagar | Normal → venda paga"),
        new("Status da venda",       "Verde → caixa aberto e pronto | Cinza → bloqueado"),
        new("Título da venda",       "Mostra N itens e total enquanto há itens no carrinho"),
        new("Fundo Desc. item",      "Âmbar quando desconto > 0"),
        new("Desc. total (Pagamento)","Âmbar quando desconto > 0"),
        new("Banner superior",       "Oculto: tudo ok | Amarelo: aviso | Vermelho: offline"),
        new("Barra de status",       "Operador, caixa, sync, versão e relógio na parte inferior"),
        new("Comprovante sync",      "Atualiza a cada 8 s: Pendente → Enviado → Sincronizado"),
        new("Vendas canceladas",     "Itálico cinza na lista de vendas do caixa"),
        new("Botão F9 Reimprimir",   "Aparece no cabeçalho após a 1ª venda finalizada"),
    ];

    public HelpWindow()
    {
        InitializeComponent();
        GlobalShortcutsGrid.ItemsSource = GlobalShortcuts;
        FieldShortcutsGrid.ItemsSource = FieldShortcuts;
        VisualCuesGrid.ItemsSource = VisualCues;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}

internal sealed record HelpRow(string Key, string Condition, string Action)
{
    internal HelpRow(string key, string action) : this(key, string.Empty, action) { }
}
