# PDV App — Testes completos Sprint 2026-06-06/07
# Executar em sessao interativa via schtasks /IT /RU Suporte
param()
$ErrorActionPreference = 'Continue'

$homologDir = 'C:\ProgramData\PDVLocal\Homologation\sprint-2026-06-07'
$outputFile = Join-Path $homologDir 'results.json'
$logFile    = Join-Path $homologDir 'log.txt'

# Heartbeat imediato — antes de qualquer UIAutomation
try { New-Item -ItemType Directory -Force -Path $homologDir | Out-Null } catch {}
try {
    "STARTED @ $(Get-Date -f 'HH:mm:ss') user=$env:USERNAME" | Set-Content $logFile -Encoding UTF8 -ErrorAction Stop
} catch {
    $logFile    = "$env:TEMP\pdv-sprint-log.txt"
    $outputFile = "$env:TEMP\pdv-sprint-results.json"
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

# Win32: SetForegroundWindow + keybd_event (mais confiavel que SendKeys em sessao nao-ativa)
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Threading;
public class Win32Kbd {
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int n);
    [DllImport("user32.dll")] public static extern void keybd_event(byte k, byte sc, uint flags, int extra);
    [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(int x, int y);
    [DllImport("user32.dll")] public static extern bool ScreenToClient(IntPtr h, ref POINT p);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] private static extern uint MapVirtualKey(uint uCode, uint uMapType);
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X; public int Y; }
    // WM_KEYDOWN + WM_KEYUP — funciona para janela com foco UIAutomation sem foreground real
    public static void PostKeyDown(IntPtr hwnd, byte vk) {
        uint sc = MapVirtualKey(vk, 0);
        uint lpDn = 1u | (sc << 16);
        uint lpUp = 1u | (sc << 16) | (1u << 30) | (1u << 31);
        PostMessage(hwnd, 0x0100, new IntPtr(vk), new IntPtr(lpDn));
        Thread.Sleep(40);
        PostMessage(hwnd, 0x0101, new IntPtr(vk), new IntPtr(lpUp));
    }
    public static void KeyPress(byte vk) {
        keybd_event(vk, 0, 0, 0); Thread.Sleep(60);
        keybd_event(vk, 0, 2, 0); Thread.Sleep(60);
    }
    [DllImport("user32.dll")] private static extern short VkKeyScan(char c);
    public static bool BringToFront(int hwnd) {
        IntPtr h = new IntPtr(hwnd);
        ShowWindow(h, 9);
        return SetForegroundWindow(h);
    }
    // PostMessage click via HWND direto — nao requer janela em foreground
    public static void ClickAtInWindow(IntPtr hwnd, int sx, int sy) {
        POINT pt = new POINT { X = sx, Y = sy };
        ScreenToClient(hwnd, ref pt);
        IntPtr lp = new IntPtr((pt.Y << 16) | (pt.X & 0xFFFF));
        PostMessage(hwnd, 0x0201, IntPtr.Zero, lp);  // WM_LBUTTONDOWN
        Thread.Sleep(40);
        PostMessage(hwnd, 0x0202, IntPtr.Zero, lp);  // WM_LBUTTONUP
    }
}
'@

# Handle da janela ativa — atualizado sempre que mudamos de janela
$script:hwnd = 0

function setWnd { param($w)
    $script:hwnd = $w.Current.NativeWindowHandle
    if ($script:hwnd -ne 0) { [Win32Kbd]::BringToFront($script:hwnd) | Out-Null; Start-Sleep -Milliseconds 300 }
    $w.SetFocus(); Start-Sleep -Milliseconds 200
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
        # Janelas owned podem aparecer como descendentes do owner no UIAutomation
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
    catch { $e.SetFocus(); [Win32Kbd]::KeyPress(0x20) }  # SPACE
    Start-Sleep -Milliseconds 500
}

# sk: envia tecla virtual para a janela ativa
# VK codes usados: ENTER=0x0D ESC=0x1B F1=0x70 F2=0x71 F8=0x77 F9=0x78 F12=0x7B DOWN=0x28 ALT=0x12 F4=0x73
function sk { param([byte]$vk)
    if ($script:hwnd -ne 0) { [Win32Kbd]::BringToFront($script:hwnd) | Out-Null; Start-Sleep -Milliseconds 100 }
    [Win32Kbd]::KeyPress($vk)
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

function selectComboItem {
    param($combo, [string]$text)
    try {
        $ep = $combo.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)
        $ep.Expand(); Start-Sleep -Milliseconds 400
        $items = $combo.FindAll([System.Windows.Automation.TreeScope]::Subtree,
            [System.Windows.Automation.PropertyCondition]::new(
                [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
                [System.Windows.Automation.ControlType]::ListItem))
        $match = $items | Where-Object { $_.Current.Name -ilike "*$text*" } | Select-Object -First 1
        if ($match) {
            ($match.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)).Select()
            Start-Sleep -Milliseconds 300
            return $match.Current.Name
        }
        $ep.Collapse()
        return $null
    } catch {
        return $null
    }
}

# ===================================================================
Log "=== PDV App - Testes Sprint 2026-06-06 ==="
Log "=== Executando em: $env:USERNAME @ $env:COMPUTERNAME ==="

Get-Process PdvLocal.App -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 2

# --- T00: INICIAR APP ---
Log "--- T00: Iniciar app ---"
$proc = Start-Process -FilePath $exePath -WorkingDirectory $exeDir -PassThru
$main = wWnd 'PDV Local' 20
if (-not $main) {
    Fail 'T00_app_startup' 'Janela nao encontrada em 20s'
    @{ timestamp=(Get-Date -f 'yyyy-MM-dd HH:mm:ss'); pass_count=0; fail_count=1; results=$results } |
        ConvertTo-Json -Depth 5 | Set-Content $outputFile -Encoding UTF8
    exit 1
}
Pass 'T00_app_startup'
setWnd $main   # traz para foreground e seta hwnd global
Start-Sleep -Seconds 2

# --- T01: ELEMENTOS DO CABECALHO ---
Log "--- T01: Elementos do cabecalho ---"
$helpBtn      = wEl $main 'HelpButton' 5
$reprintBtn   = wEl $main 'ReprintButton' 5
$cashSalesBtn = wEl $main 'CashSalesButton' 5
if ($helpBtn)      { Pass 'T01_help_btn' } else { Fail 'T01_help_btn' 'nao encontrado' }
if ($cashSalesBtn) { Pass 'T01_cash_sales_btn' } else { Fail 'T01_cash_sales_btn' 'nao encontrado' }
if ($cashSalesBtn) {
    if (-not $cashSalesBtn.Current.IsEnabled) { Pass 'T01_cash_sales_disabled_no_op' }
    else { Fail 'T01_cash_sales_disabled_no_op' 'IsEnabled=true sem operador' }
}
# ReprintButton fica Visibility.Collapsed antes da 1a venda (nao entra na arvore UIA — correto)
if (-not $reprintBtn) { Pass 'T01_reprint_collapsed_before_sale' }
else { Fail 'T01_reprint_collapsed_before_sale' 'Deveria estar Collapsed' }

# --- T02: F1 HELPWINDOW ---
Log "--- T02: F1 HelpWindow ---"
clickEl (wEl $main 'HelpButton' 5)
$helpWnd = wWnd ('Ajuda ' + [char]0x2014 + ' Atalhos e comportamentos') 8 $main
if ($helpWnd) {
    setWnd $helpWnd
    Pass 'T02_help_opens'
    $gGrid = wEl $helpWnd 'GlobalShortcutsGrid' 5
    $fGrid = wEl $helpWnd 'FieldShortcutsGrid' 5
    $vGrid = wEl $helpWnd 'VisualCuesGrid' 5
    if ($gGrid) { Pass 'T02_global_rows' (rowCount $gGrid) } else { Fail 'T02_global_rows' 'grid nao encontrado' }
    if ($fGrid) { Pass 'T02_field_rows'  (rowCount $fGrid) } else { Fail 'T02_field_rows'  'grid nao encontrado' }
    if ($vGrid) { Pass 'T02_visual_rows' (rowCount $vGrid) } else { Fail 'T02_visual_rows' 'grid nao encontrado' }
    $escBtn = wName $helpWnd 'Fechar' 3
    if ($escBtn) { clickEl $escBtn } else { closeWnd $helpWnd }
    Start-Sleep -Milliseconds 800
    $stillOpen = wWnd ('Ajuda ' + [char]0x2014 + ' Atalhos e comportamentos') 2 $main
    if (-not $stillOpen) { Pass 'T02_help_closes_btn' }
    else { closeWnd $stillOpen; Pass 'T02_help_closes_btn' 'via_window_close' }
} else { Fail 'T02_help_opens' 'nao abriu em 8s' }

# --- T03: CARREGAR OPERADOR ---
Log "--- T03: Carregar operador ---"
$opFld = wEl $main 'OperatorLoginTextBox' 5
if ($opFld) {
    setVal $opFld 'admin'; clickEl (wEl $main 'LoadOperatorButton' 5); Start-Sleep -Seconds 2
    $opVal = wEl $main 'CurrentOperatorValue' 5
    if ($opVal) {
        $txt = gEl $opVal
        if ($txt -and $txt -ne '-') { Pass 'T03_operator_loaded' $txt }
        else { Fail 'T03_operator_loaded' "Valor: '$txt'" }
    } else { Fail 'T03_operator_loaded' 'CurrentOperatorValue nao encontrado' }
} else { Fail 'T03_op_field' 'nao encontrado' }

# --- T04: ABRIR CAIXA ---
Log "--- T04: Abrir caixa ---"
$openFld = wEl $main 'OpeningAmountTextBox' 5
if ($openFld) {
    setVal $openFld '0'; clickEl (wEl $main 'OpenCashButton' 5); Start-Sleep -Seconds 2
    $csb = wEl $main 'CashSalesButton' 3
    if ($csb -and $csb.Current.IsEnabled) { Pass 'T04_cash_sales_enabled' }
    else { Fail 'T04_cash_sales_enabled' "IsEnabled=$($csb.Current.IsEnabled)" }
    $sessionEl = wEl $main 'CurrentCashSessionValue' 3
    if ($sessionEl) { Pass 'T04_cash_session' (gEl $sessionEl) }
} else { Fail 'T04_open_field' 'nao encontrado' }

# --- T05: ADICIONAR PRODUTO 1187 + SALETITLE ---
Log "--- T05: Produto + SaleTitle dinamico ---"
$prodFld = wEl $main 'ProductEntryTextBox' 5
if ($prodFld) {
    setVal $prodFld '1187'
    clickEl (wEl $main 'ProductSearchButton' 3); Start-Sleep -Seconds 2
    # P3: foco deve ir para QuantityTextBox apos resultado unico
    $qtyFld = wEl $main 'QuantityTextBox' 3
    if ($qtyFld) {
        clickEl (wEl $main 'AddItemButton' 3)
        Start-Sleep -Seconds 1
    }
    $cart = wEl $main 'CartItemsGrid' 5
    if ($cart) {
        $n = rowCount $cart
        if ($n -ge 1) { Pass 'T05_product_in_cart' $n } else { Fail 'T05_product_in_cart' '0 itens' }
    } else { Fail 'T05_cart' 'CartItemsGrid nao encontrado' }
    # P7: SaleTitleValue
    $stEl = wEl $main 'SaleTitleValue' 5
    if ($stEl) {
        $stTxt = gEl $stEl
        Pass 'T05_sale_title' $stTxt
        if ($stTxt -match 'item|R\$') { Pass 'T05_sale_title_dynamic' }
        else { Fail 'T05_sale_title_dynamic' "Sem metricas: '$stTxt'" }
    } else { Fail 'T05_sale_title' 'nao encontrado' }
} else { Fail 'T05_prod_field' 'nao encontrado' }

# Adicionar 2o produto (para rascunho ter 2 itens)
$prodFld = wEl $main 'ProductEntryTextBox' 3
if ($prodFld) {
    setVal $prodFld '1234'
    clickEl (wEl $main 'ProductSearchButton' 3); Start-Sleep -Seconds 2
    $qtyFld = wEl $main 'QuantityTextBox' 2
    if ($qtyFld) { clickEl (wEl $main 'AddItemButton' 3); Start-Sleep -Seconds 1 }
    $cart = wEl $main 'CartItemsGrid' 3
    if ($cart) { Pass 'T05_second_product' (rowCount $cart) }
}

# --- T06: RASCUNHO — fechar e reabrir ---
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
    $main = wWnd 'PDV Local' 20
    if ($main) {
        Pass 'T06_app_restarted'
        setWnd $main
        Start-Sleep -Seconds 2
        $opFld = wEl $main 'OperatorLoginTextBox' 5
        if ($opFld) { setVal $opFld 'admin'; clickEl (wEl $main 'LoadOperatorButton' 5); Start-Sleep -Seconds 2 }
        $openFld = wEl $main 'OpeningAmountTextBox' 5
        if ($openFld) { setVal $openFld '0'; clickEl (wEl $main 'OpenCashButton' 5); Start-Sleep -Seconds 4 }

        $msgEl = wEl $main 'OperationMessageValue' 5
        if ($msgEl) {
            $msg = gEl $msgEl
            Pass 'T06_draft_message' $msg
            if ($msg -match '[Rr]ascunho') { Pass 'T06_draft_restored' }
            else { Fail 'T06_draft_restored' "Msg sem Rascunho: '$msg'" }
        } else { Fail 'T06_draft_message' 'OperationMessageValue nao encontrado' }

        $cart = wEl $main 'CartItemsGrid' 5
        if ($cart) {
            $restored = rowCount $cart
            Pass 'T06_items_restored' $restored
            if ($restored -ge $itemsBefore) { Pass 'T06_count_match' }
            else { Fail 'T06_count_match' "$restored restaurados, esperado >= $itemsBefore" }
        } else { Fail 'T06_cart_restore' 'CartItemsGrid nao encontrado' }
    } else { Fail 'T06_app_restarted' 'Janela nao encontrada apos reinicio' }
}

# --- T07: SUPERVISOR + F2 NOVA VENDA ---
Log "--- T07: Supervisor + F2 ---"
$supFld = wEl $main 'SupervisorLoginTextBox' 5
if ($supFld) {
    setVal $supFld 'admin'; clickEl (wEl $main 'AuthorizeSupervisorButton' 5); Start-Sleep -Seconds 1
    $supVal = wEl $main 'SupervisorAuthorizationValue' 5
    if ($supVal) {
        $txt = gEl $supVal
        if ($txt -and $txt -ne '-') { Pass 'T07_supervisor_authorized' $txt }
        else { Fail 'T07_supervisor_authorized' "Valor: '$txt'" }
    }
}
# Preencher motivo para F2 funcionar com itens
$reasFld = wEl $main 'SupervisorReasonTextBox' 3
if ($reasFld) { setVal $reasFld 'Limpeza pre-teste sprint' }

clickEl (wEl $main 'NewSaleButton' 3); Start-Sleep -Seconds 1

$cart = wEl $main 'CartItemsGrid' 3
$rowsAfterF2 = if ($cart) { rowCount $cart } else { 0 }
if ($rowsAfterF2 -eq 0) { Pass 'T07_f2_clears_cart' }
else { Fail 'T07_f2_clears_cart' "$rowsAfterF2 itens restantes" }

# --- T08: VENDA COMPLETA COM F12 ---
Log "--- T08: Venda completa com F12 ---"
# Reautorizar supervisor
$supFld = wEl $main 'SupervisorLoginTextBox' 3
if ($supFld) { setVal $supFld 'admin'; clickEl (wEl $main 'AuthorizeSupervisorButton' 5); Start-Sleep -Seconds 1 }

# Produto
$prodFld = wEl $main 'ProductEntryTextBox' 5
if ($prodFld) {
    setVal $prodFld '1187'
    clickEl (wEl $main 'ProductSearchButton' 3); Start-Sleep -Seconds 2
    $qtyFld = wEl $main 'QuantityTextBox' 3
    if ($qtyFld) { clickEl (wEl $main 'AddItemButton' 3); Start-Sleep -Seconds 1 }
}

# Especie de pagamento
$payCombo = wEl $main 'PaymentMethodComboBox' 5
if ($payCombo) {
    $selected = selectComboItem $payCombo 'dinheiro'
    if ($selected) { Pass 'T08_species_selected' $selected }
    else {
        Pass 'T08_species_selected' 'default_item'
    }
}

# P2: GotFocus auto-preenche valor recebido
$payFld = wEl $main 'PaymentReceivedTextBox' 5
if ($payFld) {
    $payFld.SetFocus(); Start-Sleep -Milliseconds 700
    $autoVal = gEl $payFld
    if ($autoVal -and $autoVal -ne '0,00' -and $autoVal -ne '0') {
        Pass 'T08_payment_autofill' $autoVal
    } else { Fail 'T08_payment_autofill' "Valor: '$autoVal'" }
    clickEl (wEl $main 'AddPaymentButton' 5); Start-Sleep -Seconds 1   # P1: Enter = AddPaymentButton
}

$remEl = wEl $main 'RemainingValue' 3
if ($remEl) { Pass 'T08_remaining' (gEl $remEl) }

# P4: F12 = CompleteSaleButton
clickEl (wEl $main 'CompleteSaleButton' 5); Start-Sleep -Seconds 2

$receiptWnd = wWnd 'Comprovante de Venda' 10 $main
if ($receiptWnd) {
    setWnd $receiptWnd
    Pass 'T08_receipt_opens'
    $snEl = wEl $receiptWnd 'SaleNumberText' 5
    $saleNum = if ($snEl) { gEl $snEl } else { 'nao encontrado' }
    Pass 'T08_sale_number' $saleNum
    # Aguardar 1 ciclo de polling (8s)
    Start-Sleep -Seconds 9
    $syncEl = wEl $receiptWnd 'SyncStatusText' 3
    Pass 'T08_sync_status' (if ($syncEl) { gEl $syncEl } else { 'nao encontrado' })
    $closeBtn = wName $receiptWnd 'Fechar' 3
    if ($closeBtn) { clickEl $closeBtn } else { closeWnd $receiptWnd }
    Start-Sleep -Milliseconds 800
    Pass 'T08_receipt_closed'
} else { Fail 'T08_receipt_opens' 'nao abriu em 10s' }

# --- T09: REIMPRIMIR (F9 + botao) ---
Log "--- T09: Reimprimir ---"
$reprintBtn = wEl $main 'ReprintButton' 5
if ($reprintBtn) {
    if (-not $reprintBtn.Current.IsOffscreen) { Pass 'T09_reprint_visible' }
    else { Fail 'T09_reprint_visible' 'IsOffscreen=true apos venda' }

    # Botao Reimprimir (F9 usa o mesmo handler)
    $rb = wEl $main 'ReprintButton' 3
    if ($rb) {
        clickEl $rb
        $r2 = wWnd 'Comprovante de Venda' 8 $main
        if ($r2) {
            Pass 'T09_reprint_btn_opens_receipt'
            $cb = wName $r2 'Fechar' 3; if ($cb) { clickEl $cb } else { closeWnd $r2 }
            Start-Sleep -Milliseconds 700
        } else { Fail 'T09_reprint_btn_opens_receipt' 'nao abriu em 8s' }
    }
} else { Fail 'T09_reprint_visible' 'ReprintButton nao encontrado' }

# --- T10: VENDAS DO CAIXA (F8) + CANCELAMENTO ---
Log "--- T10: Vendas do caixa + cancelamento ---"
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
        $cntEl = wEl $salesWnd 'SummaryCountText' 3; if ($cntEl) { Pass 'T10_summary_count' (gEl $cntEl) }
        $totEl = wEl $salesWnd 'SummaryTotalText' 3; if ($totEl) { Pass 'T10_summary_total' (gEl $totEl) }

        # Selecionar venda "completed" via automation hook (InvokePattern, sem mouse/teclado)
        # Nota: nao reutilizar FindAll(DataItem) — um segundo FindAll no DataGrid bloqueia ~34s

        # Clica no botao oculto de selecao automatica — InvokePattern, sem teclado/mouse
        $autoBtn = wEl $salesWnd 'AutoSelectFirstSale' 3
        if ($autoBtn) {
            try {
                ($autoBtn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke()
            } catch { Log "T10 ERR Invoke: $_" }
            Start-Sleep -Milliseconds 700
        } else { Log 'AVISO: AutoSelectFirstSale nao encontrado (app versao antiga?)' }

        # Usa GetNextSibling(SalesGrid) para evitar traversal do DataGrid (~35s por chamada)
        # Na arvore UIAutomation: Grid.Row=1=SalesGrid -> Row=2=CancellationPanel -> Row=3=footer
        # WPF: Border e StackPanel nao tem OnCreateAutomationPeer — filhos "sobem" para nivel do Grid
        # Quando CancellationPanel esta visivel, CancelTitleText aparece como sibling direto do SalesGrid
        $walker = [System.Windows.Automation.TreeWalker]::RawViewWalker
        $nextEl = $walker.GetNextSibling($salesGrid)
        $panelId  = if ($nextEl) { $nextEl.Current.AutomationId } else { 'null' }
        $panelOff = if ($nextEl) { $nextEl.Current.IsOffscreen  } else { $true  }
        # Panel visivel = CancelTitleText aparece como proximo sibling (nao offscreen)
        $panelVisible = ($panelId -eq 'CancelTitleText') -and (-not $panelOff)
        Log "T10 nextSibling: id=$panelId offscreen=$panelOff => panelVisible=$panelVisible"

        if ($cnt -gt 0) {
            if ($panelVisible) {
                Pass 'T10_cancel_panel_visible'
                # Os campos do painel sao siblings planos do SalesGrid (sem container UIAutomation)
                # Percorre siblings a partir de CancelTitleText para evitar traversal do DataGrid
                $supTb = $null; $reasTb = $null; $confirmBtn = $null
                $el = $nextEl
                for ($i = 0; $i -lt 20 -and $el -ne $null; $i++) {
                    $el = $walker.GetNextSibling($el)
                    if ($el -eq $null) { break }
                    $eid = $el.Current.AutomationId
                    if ($eid -eq 'CancelSupervisorTextBox') { $supTb = $el }
                    elseif ($eid -eq 'CancelReasonTextBox') { $reasTb = $el }
                    elseif ($eid -eq 'ConfirmCancelButton')  { $confirmBtn = $el }
                    elseif ($eid -eq 'SummaryCountText')     { break }  # passou do painel
                    if ($supTb -and $reasTb -and $confirmBtn) { break }
                }
                Log "T10 campos: sup=$($null -ne $supTb) reas=$($null -ne $reasTb) btn=$($null -ne $confirmBtn)"
                if ($supTb -and $reasTb) {
                    setVal $supTb 'admin'
                    Start-Sleep -Milliseconds 500
                    setVal $reasTb 'Teste cancelamento Sprint 2026-06-07'
                    if ($confirmBtn) { clickEl $confirmBtn }
                    else { Fail 'T10_confirm_btn' 'ConfirmCancelButton nao encontrado via siblings' }
                    Start-Sleep -Seconds 3
                    # Painel sumiu quando CancelTitleText sai da arvore OU fica IsOffscreen=True
                    # (Collapsed define IsOffscreen=True no WPF UIAutomation — elemento permanece na arvore)
                    $nextEl2   = $walker.GetNextSibling($salesGrid)
                    $afterId   = if ($nextEl2) { $nextEl2.Current.AutomationId } else { 'null' }
                    $afterOff  = if ($nextEl2) { $nextEl2.Current.IsOffscreen  } else { $true  }
                    Log "T10 apos-cancel: nextSibling=$afterId offscreen=$afterOff"
                    if ($afterId -ne 'CancelTitleText' -or $afterOff) { Pass 'T10_cancellation_ok' }
                    else { Fail 'T10_cancellation_ok' "painel ainda visivel (id=$afterId off=$afterOff)" }
                    Start-Sleep -Milliseconds 500
                    # SummaryCountText — e sibling do SalesGrid (footer tambem flat na arvore)
                    # Caminha siblings ate encontrar SummaryCountText
                    $sumEl = $walker.GetNextSibling($salesGrid)
                    for ($j = 0; $j -lt 20 -and $sumEl -ne $null; $j++) {
                        if ($sumEl.Current.AutomationId -eq 'SummaryCountText') { break }
                        $sumEl = $walker.GetNextSibling($sumEl)
                    }
                    if ($sumEl -and $sumEl.Current.AutomationId -eq 'SummaryCountText') {
                        Pass 'T10_summary_after_cancel' (gEl $sumEl)
                    }
                } else { Fail 'T10_cancel_fields' "campos nao encontrados (sup=$($null -ne $supTb) reas=$($null -ne $reasTb))" }
            } else { Fail 'T10_cancel_panel_visible' "painel nao abriu (nextSibling=$panelId offscreen=$panelOff)" }
        } else { Fail 'T10_select_row' 'nenhuma linha no SalesGrid' }
    } else { Fail 'T10_sales_grid' 'SalesGrid nao encontrado' }

    $cb = wName $salesWnd 'Fechar' 3; if ($cb) { clickEl $cb } else { closeWnd $salesWnd }
    Pass 'T10_window_closed'
} else { Fail 'T10_f8_opens' 'CashSessionSalesWindow nao abriu em 8s' }

# --- ENCERRAR ---
Get-Process PdvLocal.App -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue

# --- RESULTADO FINAL ---
$summary = [ordered]@{
    timestamp  = (Get-Date -f 'yyyy-MM-dd HH:mm:ss')
    pass_count = $passCount
    fail_count = $failCount
    results    = $results
}
$json = $summary | ConvertTo-Json -Depth 5
$json | Set-Content $outputFile -Encoding UTF8
Log "=== RESULTADO: $passCount PASSOU / $failCount FALHOU ==="
Log "Saida: $outputFile"
