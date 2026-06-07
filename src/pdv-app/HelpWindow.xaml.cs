using System.Windows;

namespace PdvLocal.App;

public partial class HelpWindow : Window
{
    private static readonly List<HelpRow> GlobalShortcuts =
    [
        new("F1",    "Qualquer tela",           "Abrir esta janela de ajuda"),
        new("F2",    "Qualquer tela",           "Nova venda (exige supervisor se houver itens)"),
        new("F8",    "Caixa aberto",            "Abrir consulta de vendas do caixa"),
        new("F9",    "Após 1ª venda",           "Reimprimir último comprovante de venda"),
        new("F12",   "Qualquer tela",           "Finalizar venda"),
        new("Esc",   "Produto selecionado",     "Cancelar seleção de produto e limpar campos"),
    ];

    private static readonly List<HelpRow> FieldShortcuts =
    [
        new("Enter", "Login operador",          "Carregar operador"),
        new("Enter", "Valor de abertura",       "Abrir caixa"),
        new("Enter", "Valor de fechamento",     "Fechar caixa"),
        new("Enter", "Campo Produto",           "Buscar produto / adicionar ao carrinho"),
        new("↑  ↓",  "Grid de resultados",     "Navegar entre produtos encontrados"),
        new("Enter", "Grid de resultados",      "Selecionar produto destacado"),
        new("Enter", "Quantidade",              "Adicionar item ao carrinho"),
        new("Enter", "Desconto do item",        "Adicionar item ao carrinho"),
        new("Delete","Item no carrinho",        "Remover item (exige supervisor e motivo)"),
        new("Enter", "Valor recebido",          "Adicionar pagamento"),
        new("Delete","Pagamento na grade",      "Remover pagamento (exige supervisor e motivo)"),
        new("Enter", "Login supervisor",        "Autorizar supervisor"),
        new("Enter", "Login supervisor (cancel.)","Avançar para campo Motivo"),
        new("Enter", "Motivo (cancelamento)",   "Confirmar cancelamento de venda"),
    ];

    private static readonly List<HelpRow> VisualCues =
    [
        new("Campo Restante",        "Vermelho → valor ainda a pagar | Preto → venda paga"),
        new("Status da venda",       "Verde → caixa aberto e pronto | Cinza → bloqueado"),
        new("Autorização supervisor","Verde → autorizado para a sessão | Cinza → aguardando"),
        new("Título da venda",       "Mostra N itens e total enquanto há itens no carrinho"),
        new("Fundo Desc. item",      "Âmbar quando desconto > 0"),
        new("Fundo Desc. total",     "Âmbar quando desconto > 0"),
        new("Banner superior",       "Verde: ok | Azul: pendentes | Amarelo: aviso | Vermelho: offline"),
        new("Comprovante sync",      "Atualiza a cada 8 s: Pendente → Enviado → Sincronizado"),
        new("Vendas canceladas",     "Itálico cinza na lista de vendas do caixa"),
        new("Botão Reimprimir",      "Aparece no cabeçalho após a 1ª venda finalizada"),
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
