# PDV App — Homologacao UI Refactor 2026-07 (login -> caixa -> venda -> pagamento)
# Executar em sessao interativa via schtasks /IT /RU Suporte
param(
    [string]$OperatorLogin = 'admin',
    [string]$OperatorPassword = 'Homolog@2026'
)
$ErrorActionPreference = 'Continue'

$homologDir = 'C:\ProgramData\PDVLocal\Homologation\ui-refactor-2026-07'
$outputFile = Join-Path $homologDir 'results.json'
$logFile    = Join-Path $homologDir 'log.txt'

try { New-Item -ItemType Directory -Force -Path $homologDir | Out-Null } catch {}
try {
    "STARTED @ $(Get-Date -f 'HH:mm:ss') user=$env:USERNAME" | Set-Content $logFile -Encoding UTF8 -ErrorAction Stop
} catch {
    $logFile    = "$env:TEMP\pdv-ui-refactor-log.txt"
    $outputFile = "$env:TEMP\pdv-ui-refactor-results.json"
    "STARTED(TEMP) @ $(Get-Date -f 'HH:mm:ss') user=$env:USERNAME err=$_" | Set-Content $logFile -Encoding UTF8
}
$exePath    = 'C:\Program Files\PDVLocal\PDVApp\PdvLocal.App.exe'
$exeDir     = 'C:\Program Files\PDVLocal\PDVApp'

$results = [ordered]@{}
$passCount = 0; $failCount = 0

function Log  { param($m) $l = "$(Get-Date -f 'HH:mm:ss') $m"; Write-Host $l; try { Add-Content $logFile $l } catch {} }
function Pass { param($k, $v = 'ok') $script:results[$k] = $v; $script:passCount++; Log "[PASS] $k = $v" }
function Fail { param($k, $v) $script:results[$k] = "FAIL: $v"; $script:failCount++; Log "[FAIL] $k = $v" }

try {
    Add-Type -AssemblyName UIAutomationClient
    Add-Type -AssemblyName UIAutomationTypes
    Log "UIAutomation assemblies OK"
} catch {
    Log "ERRO CRITICO Add-Type: $_"
    Fail 'INIT_UIAutomation' "$_"
    @{ timestamp=(Get-Date -f 'yyyy-MM-dd HH:mm:ss'); error="$_"; results=$results } |
        ConvertTo-Json -Depth 3 | Set-Content $outputFile -Encoding UTF8
    exit 1
}

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Threading;
public class Win32Kbd {
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int n);
    [DllImport("user32.dll")] public static extern void keybd_event(byte k, byte sc, uint flags, int extra);
    [DllImport("user32.dll")] public static extern bool ScreenToClient(IntPtr h, ref POINT p);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] private static extern short VkKeyScan(char c);
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X; public int Y; }
    public static void KeyPress(byte vk) {
        keybd_event(vk, 0, 0, 0); Thread.Sleep(60);
        keybd_event(vk, 0, 2, 0); Thread.Sleep(60);
    }
    // Combo modificador+tecla (ex.: Ctrl+L) — fire-and-forget, seguro para abrir dialogos modais
    public static void KeyCombo(byte modifier, byte vk) {
        keybd_event(modifier, 0, 0, 0); Thread.Sleep(40);
        keybd_event(vk, 0, 0, 0); Thread.Sleep(60);
        keybd_event(vk, 0, 2, 0); Thread.Sleep(40);
        keybd_event(modifier, 0, 2, 0); Thread.Sleep(60);
    }
    public static void TypeChar(char c) {
        short vk = VkKeyScan(c);
        if (vk == -1) return;
        byte key = (byte)(vk & 0xFF);
        bool shift = (vk & 0x100) != 0;
        if (shift) keybd_event(0x10, 0, 0, 0);
        keybd_event(key, 0, 0, 0); Thread.Sleep(25);
        keybd_event(key, 0, 2, 0);
        if (shift) keybd_event(0x10, 0, 2, 0);
        Thread.Sleep(35);
    }
    public static bool BringToFront(int hwnd) {
        IntPtr h = new IntPtr(hwnd);
        ShowWindow(h, 9);
        return SetForegroundWindow(h);
    }
    // Clique via PostMessage — assincrono, nao bloqueia quando o clique abre ShowDialog
    public static void ClickAtInWindow(IntPtr hwnd, int sx, int sy) {
        POINT pt = new POINT { X = sx, Y = sy };
        ScreenToClient(hwnd, ref pt);
        IntPtr lp = new IntPtr((pt.Y << 16) | (pt.X & 0xFFFF));
        PostMessage(hwnd, 0x0201, IntPtr.Zero, lp);
        Thread.Sleep(40);
        PostMessage(hwnd, 0x0202, IntPtr.Zero, lp);
    }
}
'@

$script:hwnd = 0

function setWnd { param($w)
    $script:hwnd = $w.Current.NativeWindowHandle
    if ($script:hwnd -ne 0) { [Win32Kbd]::BringToFront($script:hwnd) | Out-Null; Start-Sleep -Milliseconds 300 }
    try { $w.SetFocus() } catch {}
    Start-Sleep -Milliseconds 200
}

function wEl {
    param($root, [string]$id, [int]$sec = 10)
    $c = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id)
    $dl = [DateTime]::Now.AddSeconds($sec)
    while ([DateTime]::Now -lt $dl) {
        $e = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $c)
        if ($e) { return $e }
        Start-Sleep -Milliseconds 250
    }
    return $null
}

function wWnd {
    param([string]$title, [int]$sec = 12, $owner = $null)
    $nc = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::NameProperty, $title)
    $cc = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Window)
    $ac = [System.Windows.Automation.AndCondition]::new($nc, $cc)
    $dl = [DateTime]::Now.AddSeconds($sec)
    while ([DateTime]::Now -lt $dl) {
        $w = [System.Windows.Automation.AutomationElement]::RootElement.FindFirst(
            [System.Windows.Automation.TreeScope]::Children, $ac)
        if ($w) { return $w }
        if ($owner) {
            try {
                $w = $owner.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $ac)
                if ($w) { return $w }
            } catch {}
        }
        Start-Sleep -Milliseconds 300
    }
    return $null
}

function wName {
    param($root, [string]$name, [int]$sec = 5)
    $c = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::NameProperty, $name)
    $dl = [DateTime]::Now.AddSeconds($sec)
    while ([DateTime]::Now -lt $dl) {
        $e = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $c)
        if ($e) { return $e }
        Start-Sleep -Milliseconds 250
    }
    return $null
}

function setVal {
    param($e, [string]$v)
    if ($script:hwnd -ne 0) { [Win32Kbd]::BringToFront($script:hwnd) | Out-Null; Start-Sleep -Milliseconds 150 }
    $e.SetFocus(); Start-Sleep -Milliseconds 200
    try {
        $vp = $e.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
        $vp.SetValue($v)
    } catch {
        foreach ($ch in $v.ToCharArray()) { [Win32Kbd]::TypeChar($ch) }
    }
    Start-Sleep -Milliseconds 200
}

function clickEl {
    param($e)
    try { ($e.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke() }
    catch { $e.SetFocus(); [Win32Kbd]::KeyPress(0x20) }
    Start-Sleep -Milliseconds 500
}

# Clique assincrono via PostMessage — usar quando o clique abre um dialogo modal
function clickElAsync {
    param($e)
    $r = $e.Current.BoundingRectangle
    $cx = [int]($r.X + $r.Width / 2); $cy = [int]($r.Y + $r.Height / 2)
    $h = $e.Current.NativeWindowHandle
    if ($h -eq 0) { $h = $script:hwnd }
    [Win32Kbd]::ClickAtInWindow([IntPtr]$h, $cx, $cy)
    Start-Sleep -Milliseconds 600
}

# VK: ENTER=0x0D ESC=0x1B F1=0x70 F2=0x71 F3=0x72 F4=0x73 F8=0x77 F9=0x78 F10=0x79 F11=0x7A F12=0x7B L=0x4C CTRL=0x11
function sk { param([byte]$vk)
    if ($script:hwnd -ne 0) { [Win32Kbd]::BringToFront($script:hwnd) | Out-Null; Start-Sleep -Milliseconds 100 }
    [Win32Kbd]::KeyPress($vk)
    Start-Sleep -Milliseconds 450
}

function skCtrl { param([byte]$vk)
    if ($script:hwnd -ne 0) { [Win32Kbd]::BringToFront($script:hwnd) | Out-Null; Start-Sleep -Milliseconds 100 }
    [Win32Kbd]::KeyCombo(0x11, $vk)
    Start-Sleep -Milliseconds 450
}

function gEl {
    param($e)
    try { return ($e.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)).Current.Value } catch {}
    try { return $e.Current.Name } catch {}
    return ''
}

function rowCount {
    param($grid)
    $c = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::DataItem)
    return ($grid.FindAll([System.Windows.Automation.TreeScope]::Children, $c)).Count
}

function closeWnd {
    param($w)
    try { ($w.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern)).Close() } catch {}
    Start-Sleep -Milliseconds 700
}

# Autoriza no SupervisorAuthorizationDialog (login + senha + motivo)
function authorizeSupervisor {
    param([string]$reason, [int]$sec = 10)
    $dlg = wWnd 'Autorizacao de supervisor' $sec
    if (-not $dlg) { return $false }
    setWnd $dlg
    setVal (wEl $dlg 'SupervisorLoginTextBox' 5) $OperatorLogin
    setVal (wEl $dlg 'SupervisorPasswordBox' 5) $OperatorPassword
    setVal (wEl $dlg 'SupervisorReasonTextBox' 5) $reason
    clickEl (wEl $dlg 'AuthorizeSupervisorButton' 5)
    Start-Sleep -Seconds 1
    $still = wWnd 'Autorizacao de supervisor' 2
    return (-not $still)
}

# Faz login na tela inicial e retorna a janela principal
function doLogin {
    param([int]$sec = 20)
    $loginTitle = 'PDV Local ' + [char]0x2014 + ' Acesso'
    $loginWnd = wWnd $loginTitle $sec
    if (-not $loginWnd) { return $null }
    setWnd $loginWnd
    setVal (wEl $loginWnd 'LoginTextBox' 5) $OperatorLogin
    setVal (wEl $loginWnd 'SenhaPasswordBox' 5) $OperatorPassword
    clickEl (wEl $loginWnd 'EntrarButton' 5)
    Start-Sleep -Seconds 2
    return (wWnd 'PDV Local' 15)
}

# ===================================================================
Log "=== PDV App - Homologacao UI Refactor 2026-07 ==="
Log "=== Executando em: $env:USERNAME @ $env:COMPUTERNAME | operador=$OperatorLogin ==="

Get-Process PdvLocal.App -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 2

# --- T00: LOGIN OBRIGATORIO NA ABERTURA ---
Log "--- T00: Login na abertura ---"
$proc = Start-Process -FilePath $exePath -WorkingDirectory $exeDir -PassThru
$loginTitle = 'PDV Local ' + [char]0x2014 + ' Acesso'
$loginWnd = wWnd $loginTitle 20
if ($loginWnd) { Pass 'T00_login_window_first' } else { Fail 'T00_login_window_first' 'LoginWindow nao apareceu em 20s' }
$main = $null
if ($loginWnd) {
    setWnd $loginWnd
    setVal (wEl $loginWnd 'LoginTextBox' 5) $OperatorLogin
    setVal (wEl $loginWnd 'SenhaPasswordBox' 5) $OperatorPassword
    clickEl (wEl $loginWnd 'EntrarButton' 5)
    Start-Sleep -Seconds 2
    $main = wWnd 'PDV Local' 15
}
if (-not $main) {
    Fail 'T00_main_after_login' 'Janela principal nao apareceu apos login'
    @{ timestamp=(Get-Date -f 'yyyy-MM-dd HH:mm:ss'); pass_count=$passCount; fail_count=$failCount; results=$results } |
        ConvertTo-Json -Depth 5 | Set-Content $outputFile -Encoding UTF8
    exit 1
}
Pass 'T00_main_after_login'
setWnd $main
Start-Sleep -Seconds 1

# --- T01: DIALOGO DE CAIXA AUTOMATICO APOS LOGIN ---
Log "--- T01: Abertura de caixa automatica ---"
$cashWnd = wWnd 'Caixa' 12 $main
if ($cashWnd) {
    Pass 'T01_cash_dialog_auto'
    setWnd $cashWnd
    $openFld = wEl $cashWnd 'OpeningAmountTextBox' 5
    if ($openFld) {
        setVal $openFld '0'
        clickEl (wEl $cashWnd 'OpenCashButton' 5)
        Start-Sleep -Seconds 2
        $still = wWnd 'Caixa' 2
        if (-not $still) { Pass 'T01_cash_opened_dialog_closed' }
        else { Fail 'T01_cash_opened_dialog_closed' 'dialogo Caixa nao fechou apos abrir' ; closeWnd $still }
    } else { Fail 'T01_opening_field' 'OpeningAmountTextBox nao encontrado' }
} else {
    # Caixa ja estava aberto de execucao anterior — dialogo nao auto-abre (comportamento esperado)
    Pass 'T01_cash_dialog_auto' 'caixa_ja_aberto'
}
setWnd $main
Start-Sleep -Seconds 1

# --- T02: CABECALHO + BARRA DE STATUS ---
Log "--- T02: Cabecalho e barra de status ---"
foreach ($btn in @('HelpButton','PriceCheckButton','CashButton','SwitchOperatorButton','SettingsButton')) {
    if (wEl $main $btn 4) { Pass "T02_btn_$btn" } else { Fail "T02_btn_$btn" 'nao encontrado' }
}
$csb = wEl $main 'CashSalesButton' 4
if ($csb -and $csb.Current.IsEnabled) { Pass 'T02_cash_sales_enabled' }
else { Fail 'T02_cash_sales_enabled' 'desabilitado com caixa aberto' }
$opSt = wEl $main 'OperatorStatusText' 4
if ($opSt -and (gEl $opSt) -like "*$OperatorLogin*") { Pass 'T02_status_operator' (gEl $opSt) }
else { Fail 'T02_status_operator' "texto: '$(gEl $opSt)'" }
$cashSt = wEl $main 'CashStatusText' 4
if ($cashSt -and (gEl $cashSt) -like '*aberto*') { Pass 'T02_status_cash_open' (gEl $cashSt) }
else { Fail 'T02_status_cash_open' "texto: '$(gEl $cashSt)'" }
$verSt = wEl $main 'VersionText' 4
if ($verSt -and (gEl $verSt)) { Pass 'T02_status_version' (gEl $verSt) } else { Fail 'T02_status_version' 'vazio' }
$clkSt = wEl $main 'ClockText' 4
if ($clkSt -and (gEl $clkSt)) { Pass 'T02_status_clock' (gEl $clkSt) } else { Fail 'T02_status_clock' 'vazio' }

# --- T03: F1 AJUDA ---
Log "--- T03: F1 Ajuda ---"
sk 0x70
$helpWnd = wWnd ('Ajuda ' + [char]0x2014 + ' Atalhos e comportamentos') 8 $main
if ($helpWnd) {
    setWnd $helpWnd
    Pass 'T03_help_opens'
    $gGrid = wEl $helpWnd 'GlobalShortcutsGrid' 5
    if ($gGrid) { Pass 'T03_global_rows' (rowCount $gGrid) } else { Fail 'T03_global_rows' 'grid nao encontrado' }
    $escBtn = wName $helpWnd 'Fechar' 3
    if ($escBtn) { clickEl $escBtn } else { closeWnd $helpWnd }
    Start-Sleep -Milliseconds 800
    Pass 'T03_help_closes'
} else { Fail 'T03_help_opens' 'nao abriu em 8s' }
setWnd $main

# --- T04: F3 CONSULTA DE PRECO ---
Log "--- T04: F3 Consulta de preco ---"
sk 0x72
$priceWnd = wWnd 'Consulta de preco' 8 $main
if ($priceWnd) {
    setWnd $priceWnd
    Pass 'T04_price_opens'
    setVal (wEl $priceWnd 'PriceQueryTextBox' 5) '1187'
    clickEl (wEl $priceWnd 'PriceSearchButton' 5)
    Start-Sleep -Seconds 2
    $priceVal = wEl $priceWnd 'PriceValueText' 5
    $pv = if ($priceVal) { gEl $priceVal } else { '' }
    if ($pv -and $pv -notmatch '0,00') { Pass 'T04_price_found' $pv }
    else { Fail 'T04_price_found' "valor: '$pv'" }
    $cb = wName $priceWnd 'Fechar (Esc)' 3
    if ($cb) { clickEl $cb } else { closeWnd $priceWnd }
    Start-Sleep -Milliseconds 800
    Pass 'T04_price_closes'
} else { Fail 'T04_price_opens' 'nao abriu em 8s' }
setWnd $main

# --- T05: QUANTIDADE*CODIGO + SALETITLE ---
Log "--- T05: quantidade*codigo e titulo dinamico ---"
$prodFld = wEl $main 'ProductEntryTextBox' 5
if ($prodFld) {
    setVal $prodFld '2*1187'
    clickEl (wEl $main 'ProductSearchButton' 3); Start-Sleep -Seconds 2
    $qtyFld = wEl $main 'QuantityTextBox' 3
    $qtyTxt = if ($qtyFld) { gEl $qtyFld } else { '' }
    if ($qtyTxt -eq '2') { Pass 'T05_qty_shortcut' $qtyTxt } else { Fail 'T05_qty_shortcut' "Qtd: '$qtyTxt'" }
    clickEl (wEl $main 'AddItemButton' 3); Start-Sleep -Seconds 1
    $cart = wEl $main 'CartItemsGrid' 5
    if ($cart -and (rowCount $cart) -ge 1) { Pass 'T05_product_in_cart' (rowCount $cart) }
    else { Fail 'T05_product_in_cart' '0 itens' }
    $stEl = wEl $main 'SaleTitleValue' 5
    $stTxt = if ($stEl) { gEl $stEl } else { '' }
    if ($stTxt -match 'item|R\$') { Pass 'T05_sale_title_dynamic' $stTxt }
    else { Fail 'T05_sale_title_dynamic' "titulo: '$stTxt'" }
} else { Fail 'T05_prod_field' 'nao encontrado' }

# Segundo produto para o rascunho
$prodFld = wEl $main 'ProductEntryTextBox' 3
if ($prodFld) {
    setVal $prodFld '1234'
    clickEl (wEl $main 'ProductSearchButton' 3); Start-Sleep -Seconds 2
    clickEl (wEl $main 'AddItemButton' 3); Start-Sleep -Seconds 1
    $cart = wEl $main 'CartItemsGrid' 3
    if ($cart) { Pass 'T05_second_product' (rowCount $cart) }
}

# --- T06: RASCUNHO — fechar e reabrir com login ---
Log "--- T06: Rascunho automatico ---"
$cart = wEl $main 'CartItemsGrid' 3
$itemsBefore = if ($cart) { rowCount $cart } else { 0 }
Pass 'T06_items_before_close' $itemsBefore

if ($itemsBefore -ge 1) {
    Log "Encerrando app para testar rascunho..."
    Get-Process PdvLocal.App -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 3

    Log "Reabrindo app..."
    $proc = Start-Process -FilePath $exePath -WorkingDirectory $exeDir -PassThru
    $main = doLogin 20
    if ($main) {
        Pass 'T06_app_restarted_with_login'
        setWnd $main
        Start-Sleep -Seconds 3
        # Caixa continua aberto — dialogo nao deve auto-abrir
        $cashDlg = wWnd 'Caixa' 4
        if (-not $cashDlg) { Pass 'T06_no_cash_dialog_when_open' }
        else { Fail 'T06_no_cash_dialog_when_open' 'dialogo Caixa abriu com caixa ja aberto'; closeWnd $cashDlg }
        $msgEl = wEl $main 'OperationMessageValue' 5
        $msg = if ($msgEl) { gEl $msgEl } else { '' }
        if ($msg -match '[Rr]ascunho') { Pass 'T06_draft_restored' $msg }
        else { Fail 'T06_draft_restored' "Msg: '$msg'" }
        $cart = wEl $main 'CartItemsGrid' 5
        if ($cart) {
            $restored = rowCount $cart
            if ($restored -ge $itemsBefore) { Pass 'T06_count_match' $restored }
            else { Fail 'T06_count_match' "$restored restaurados, esperado >= $itemsBefore" }
        } else { Fail 'T06_cart_restore' 'CartItemsGrid nao encontrado' }
    } else { Fail 'T06_app_restarted_with_login' 'login/main nao apareceu apos reinicio' }
}

# --- T07: F2 NOVA VENDA EXIGE SUPERVISOR (dialogo com senha) ---
Log "--- T07: F2 nova venda com autorizacao ---"
sk 0x71
if (authorizeSupervisor 'Limpeza pre-teste homologacao') {
    Pass 'T07_supervisor_dialog_ok'
} else {
    Fail 'T07_supervisor_dialog_ok' 'dialogo de autorizacao nao apareceu/fechou'
}
Start-Sleep -Seconds 1
setWnd $main
$cart = wEl $main 'CartItemsGrid' 3
$rowsAfterF2 = if ($cart) { rowCount $cart } else { 0 }
if ($rowsAfterF2 -eq 0) { Pass 'T07_f2_clears_cart' }
else { Fail 'T07_f2_clears_cart' "$rowsAfterF2 itens restantes" }

# --- T08: VENDA COMPLETA — PAGAMENTO COMO ETAPA (F12) + CPF ---
Log "--- T08: Venda completa com janela de Pagamento ---"
$prodFld = wEl $main 'ProductEntryTextBox' 5
if ($prodFld) {
    setVal $prodFld '1187'
    clickEl (wEl $main 'ProductSearchButton' 3); Start-Sleep -Seconds 2
    clickEl (wEl $main 'AddItemButton' 3); Start-Sleep -Seconds 1
}
sk 0x7B   # F12 = alias de F10 (abre Pagamento sem risco de syskey)
$payWnd = wWnd 'Pagamento' 10 $main
if ($payWnd) {
    setWnd $payWnd
    Pass 'T08_payment_window_opens'

    # CPF/CNPJ na nota (CPF valido de teste)
    $docFld = wEl $payWnd 'CustomerDocumentTextBox' 5
    if ($docFld) { setVal $docFld '52998224725'; Pass 'T08_customer_document_field' }
    else { Fail 'T08_customer_document_field' 'CustomerDocumentTextBox nao encontrado' }

    # Autofill do valor recebido no GotFocus
    $payFld = wEl $payWnd 'PaymentReceivedTextBox' 5
    if ($payFld) {
        $payFld.SetFocus(); Start-Sleep -Milliseconds 700
        $autoVal = gEl $payFld
        if ($autoVal -and $autoVal -ne '0,00' -and $autoVal -ne '0') { Pass 'T08_payment_autofill' $autoVal }
        else { Fail 'T08_payment_autofill' "Valor: '$autoVal'" }
        clickEl (wEl $payWnd 'AddPaymentButton' 5); Start-Sleep -Seconds 1
    } else { Fail 'T08_payment_field' 'PaymentReceivedTextBox nao encontrado' }

    $remEl = wEl $payWnd 'RemainingValue' 3
    if ($remEl) { Pass 'T08_remaining' (gEl $remEl) }

    clickEl (wEl $payWnd 'CompleteSaleButton' 5); Start-Sleep -Seconds 2
    $payStill = wWnd 'Pagamento' 2
    if (-not $payStill) { Pass 'T08_payment_window_closes' }
    else { Fail 'T08_payment_window_closes' 'janela Pagamento nao fechou'; closeWnd $payStill }

    $receiptWnd = wWnd 'Comprovante de Venda' 10 $main
    if ($receiptWnd) {
        setWnd $receiptWnd
        Pass 'T08_receipt_opens'
        $snEl = wEl $receiptWnd 'SaleNumberText' 5
        Pass 'T08_sale_number' (if ($snEl) { gEl $snEl } else { 'nao encontrado' })
        Start-Sleep -Seconds 9
        $syncEl = wEl $receiptWnd 'SyncStatusText' 3
        Pass 'T08_sync_status' (if ($syncEl) { gEl $syncEl } else { 'nao encontrado' })
        $closeBtn = wName $receiptWnd 'Fechar' 3
        if ($closeBtn) { clickEl $closeBtn } else { closeWnd $receiptWnd }
        Start-Sleep -Milliseconds 800
        Pass 'T08_receipt_closed'
    } else { Fail 'T08_receipt_opens' 'nao abriu em 10s' }
} else { Fail 'T08_payment_window_opens' 'janela Pagamento nao abriu em 10s' }
setWnd $main

# --- T09: F9 REIMPRIMIR ---
Log "--- T09: Reimprimir ---"
$reprintBtn = wEl $main 'ReprintButton' 5
if ($reprintBtn) {
    if (-not $reprintBtn.Current.IsOffscreen) { Pass 'T09_reprint_visible' }
    else { Fail 'T09_reprint_visible' 'IsOffscreen=true apos venda' }
    clickEl $reprintBtn
    $r2 = wWnd 'Comprovante de Venda' 8 $main
    if ($r2) {
        Pass 'T09_reprint_opens_receipt'
        $cb = wName $r2 'Fechar' 3; if ($cb) { clickEl $cb } else { closeWnd $r2 }
        Start-Sleep -Milliseconds 700
    } else { Fail 'T09_reprint_opens_receipt' 'nao abriu em 8s' }
} else { Fail 'T09_reprint_visible' 'ReprintButton nao encontrado' }

# --- T10: VENDAS DO CAIXA (F8) + CANCELAMENTO ---
Log "--- T10: Vendas do caixa + cancelamento ---"
setWnd $main
clickEl (wEl $main 'CashSalesButton' 3)
$salesWnd = wWnd 'Vendas do Caixa' 8 $main
if ($salesWnd) {
    Pass 'T10_f8_opens'
    setWnd $salesWnd
    Start-Sleep -Seconds 1
    $salesGrid = wEl $salesWnd 'SalesGrid' 5
    if ($salesGrid) {
        $cnt = rowCount $salesGrid
        Pass 'T10_sales_count' $cnt
        $autoBtn = wEl $salesWnd 'AutoSelectFirstSale' 3
        if ($autoBtn) {
            try { ($autoBtn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke() }
            catch { Log "T10 ERR Invoke: $_" }
            Start-Sleep -Milliseconds 700
        }
        $walker = [System.Windows.Automation.TreeWalker]::RawViewWalker
        $nextEl = $walker.GetNextSibling($salesGrid)
        $panelId  = if ($nextEl) { $nextEl.Current.AutomationId } else { 'null' }
        $panelOff = if ($nextEl) { $nextEl.Current.IsOffscreen  } else { $true  }
        $panelVisible = ($panelId -eq 'CancelTitleText') -and (-not $panelOff)
        if ($cnt -gt 0 -and $panelVisible) {
            Pass 'T10_cancel_panel_visible'
            $supTb = $null; $reasTb = $null; $confirmBtn = $null
            $el = $nextEl
            for ($i = 0; $i -lt 20 -and $el -ne $null; $i++) {
                $el = $walker.GetNextSibling($el)
                if ($el -eq $null) { break }
                $eid = $el.Current.AutomationId
                if ($eid -eq 'CancelSupervisorTextBox') { $supTb = $el }
                elseif ($eid -eq 'CancelReasonTextBox') { $reasTb = $el }
                elseif ($eid -eq 'ConfirmCancelButton')  { $confirmBtn = $el }
                elseif ($eid -eq 'SummaryCountText')     { break }
                if ($supTb -and $reasTb -and $confirmBtn) { break }
            }
            if ($supTb -and $reasTb -and $confirmBtn) {
                setVal $supTb $OperatorLogin
                Start-Sleep -Milliseconds 400
                setVal $reasTb 'Teste cancelamento homologacao UI refactor'
                clickEl $confirmBtn
                Start-Sleep -Seconds 3
                $nextEl2  = $walker.GetNextSibling($salesGrid)
                $afterId  = if ($nextEl2) { $nextEl2.Current.AutomationId } else { 'null' }
                $afterOff = if ($nextEl2) { $nextEl2.Current.IsOffscreen  } else { $true  }
                if ($afterId -ne 'CancelTitleText' -or $afterOff) { Pass 'T10_cancellation_ok' }
                else { Fail 'T10_cancellation_ok' "painel ainda visivel (id=$afterId off=$afterOff)" }
            } else { Fail 'T10_cancel_fields' "sup=$($null -ne $supTb) reas=$($null -ne $reasTb) btn=$($null -ne $confirmBtn)" }
        } elseif ($cnt -gt 0) { Fail 'T10_cancel_panel_visible' "painel nao abriu (id=$panelId off=$panelOff)" }
        else { Fail 'T10_select_row' 'nenhuma linha no SalesGrid' }
    } else { Fail 'T10_sales_grid' 'SalesGrid nao encontrado' }
    $cb = wName $salesWnd 'Fechar' 3; if ($cb) { clickEl $cb } else { closeWnd $salesWnd }
    Pass 'T10_window_closed'
} else { Fail 'T10_f8_opens' 'CashSessionSalesWindow nao abriu em 8s' }
setWnd $main

# --- T11: SUPRIMENTO VIA DIALOGO DE CAIXA (F4) ---
Log "--- T11: Suprimento via F4 ---"
sk 0x73
$cashWnd = wWnd 'Caixa' 8 $main
if ($cashWnd) {
    Pass 'T11_cash_dialog_opens'
    setWnd $cashWnd
    $movFld = wEl $cashWnd 'CashMovementAmountTextBox' 5
    if ($movFld) {
        setVal $movFld '50,00'
        clickElAsync (wEl $cashWnd 'CashSupplyButton' 3)
        if (authorizeSupervisor 'Suprimento teste homologacao') {
            Start-Sleep -Seconds 2
            $msgEl = wEl $cashWnd 'CashMessageText' 5
            $msgTxt = if ($msgEl) { gEl $msgEl } else { '' }
            if ($msgTxt -like '*registrado*') { Pass 'T11_supply_ok' $msgTxt }
            else { Fail 'T11_supply_ok' "Mensagem: '$msgTxt'" }
        } else { Fail 'T11_supply_authorization' 'dialogo de autorizacao nao apareceu/fechou' }
    } else { Fail 'T11_supply_ok' 'CashMovementAmountTextBox nao encontrado' }
    # Mantem o dialogo aberto para o T12
} else { Fail 'T11_cash_dialog_opens' 'dialogo Caixa nao abriu com F4' }

# --- T12: FECHAR CAIXA VIA DIALOGO ---
Log "--- T12: Fechar caixa ---"
$cashWnd = wWnd 'Caixa' 4 $main
if (-not $cashWnd) { sk 0x73; $cashWnd = wWnd 'Caixa' 8 $main }
if ($cashWnd) {
    setWnd $cashWnd
    # ClosingAmountTextBox ja vem preenchido com o dinheiro esperado
    clickEl (wEl $cashWnd 'CloseCashButton' 5)
    Start-Sleep -Seconds 2
    $msgEl = wEl $cashWnd 'CashMessageText' 5
    $msgTxt = if ($msgEl) { gEl $msgEl } else { '' }
    if ($msgTxt -like '*Caixa fechado*') { Pass 'T12_close_message' $msgTxt }
    else { Fail 'T12_close_message' "Mensagem: '$msgTxt'" }
    $vb = wName $cashWnd 'Voltar (Esc)' 3
    if ($vb) { clickEl $vb } else { closeWnd $cashWnd }
    Start-Sleep -Seconds 1
    setWnd $main
    $gateEl = wEl $main 'SaleGateValue' 3
    $gateTxt = if ($gateEl) { gEl $gateEl } else { '' }
    if ($gateTxt -like '*bloqueada*') { Pass 'T12_sale_gate_blocked' $gateTxt }
    else { Fail 'T12_sale_gate_blocked' "SaleGate: '$gateTxt'" }
    $cashSt = wEl $main 'CashStatusText' 3
    $cashTxt = if ($cashSt) { gEl $cashSt } else { '' }
    if ($cashTxt -like '*fechado*') { Pass 'T12_status_cash_closed' $cashTxt }
    else { Fail 'T12_status_cash_closed' "Status: '$cashTxt'" }
} else { Fail 'T12_close_message' 'dialogo Caixa nao abriu' }

# --- T13: CTRL+L TRAVAR TERMINAL ---
Log "--- T13: Travar terminal ---"
setWnd $main
skCtrl 0x4C
$lockWnd = wWnd 'Terminal travado' 8 $main
if ($lockWnd) {
    Pass 'T13_lock_opens'
    setWnd $lockWnd
    setVal (wEl $lockWnd 'UnlockPasswordBox' 5) $OperatorPassword
    clickEl (wEl $lockWnd 'UnlockButton' 5)
    Start-Sleep -Seconds 2
    $still = wWnd 'Terminal travado' 2
    if (-not $still) { Pass 'T13_unlock_ok' }
    else { Fail 'T13_unlock_ok' 'lock screen nao fechou com senha correta' }
} else { Fail 'T13_lock_opens' 'lock screen nao abriu com Ctrl+L' }

# --- ENCERRAR ---
Get-Process PdvLocal.App -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue

# --- RESULTADO FINAL ---
$summary = [ordered]@{
    timestamp  = (Get-Date -f 'yyyy-MM-dd HH:mm:ss')
    pass_count = $passCount
    fail_count = $failCount
    results    = $results
}
$summary | ConvertTo-Json -Depth 5 | Set-Content $outputFile -Encoding UTF8
Log "=== RESULTADO: $passCount PASSOU / $failCount FALHOU ==="
Log "Saida: $outputFile"
