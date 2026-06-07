using System.Diagnostics;
using System.Security.Principal;
using System.Text;

namespace SyncAgent.Installer;

public sealed class InstallerWizardForm : Form
{
    private readonly Panel _content = new() { Dock = DockStyle.Fill, Padding = new Padding(20) };
    private readonly Button _backButton = new() { Text = "Voltar", Width = 90 };
    private readonly Button _nextButton = new() { Text = "Avancar", Width = 90 };
    private readonly Button _cancelButton = new() { Text = "Cancelar", Width = 90 };
    private readonly Label _title = new() { Dock = DockStyle.Top, Height = 36, Font = new Font("Segoe UI", 14, FontStyle.Bold) };
    private readonly Label _description = new() { Dock = DockStyle.Top, Height = 42, ForeColor = Color.DimGray };

    private readonly TextBox _installRoot = new() { Text = @"C:\Program Files\PDVLocal" };
    private readonly RadioButton _useExistingPostgres = new() { Text = "Usar PostgreSQL 17 ja instalado", Checked = true };
    private readonly RadioButton _installPostgres = new() { Text = "Instalar PostgreSQL 17 automaticamente" };
    private readonly TextBox _postgresInstallRoot = new() { Text = @"C:\Program Files\PostgreSQL\17" };
    private readonly TextBox _postgresDataDirectory = new() { Text = @"C:\ProgramData\PDVLocal\PostgreSQL17\data" };
    private readonly TextBox _postgresSourceRoot = new();
    private readonly TextBox _pgVectorSourceRoot = new();
    private readonly TextBox _postgresServiceName = new() { Text = "postgresql-x64-17-pdvlocal" };
    private readonly TextBox _psqlPath = new() { Text = "psql" };
    private readonly TextBox _postgresHost = new() { Text = "localhost" };
    private readonly NumericUpDown _postgresPort = new() { Minimum = 1, Maximum = 65535, Value = 5432 };
    private readonly TextBox _postgresAdminUser = new() { Text = "postgres" };
    private readonly TextBox _postgresAdminPassword = PasswordBox("postgres");
    private readonly TextBox _databaseName = new() { Text = "pdv_sync" };
    private readonly TextBox _databaseUser = new() { Text = "pdv_sync" };
    private readonly TextBox _databasePassword = PasswordBox("pdv_sync");
    private readonly CheckBox _enableArpa = new() { Text = "Habilitar coletor Arpa nesta instalacao" };
    private readonly TextBox _arpaHost = new() { Text = "127.0.0.1" };
    private readonly NumericUpDown _arpaPort = new() { Minimum = 1, Maximum = 65535, Value = 5432 };
    private readonly TextBox _arpaDatabase = new() { Text = "control" };
    private readonly TextBox _arpaUsername = new() { Text = "sync_agent_anapolis_ro" };
    private readonly TextBox _arpaPassword = PasswordBox("postgres");
    private readonly NumericUpDown _arpaBatchSize = new() { Minimum = 1, Maximum = 50000, Value = 5000 };
    private readonly Button _prepareArpaViews = new() { Text = "Preparar views", Width = 140 };
    private readonly Button _createArpaUser = new() { Text = "Criar usuario", Width = 140 };
    private readonly Button _testArpaConnection = new() { Text = "Testar conexao", Width = 140 };
    private readonly Label _arpaStatus = new() { AutoSize = true, ForeColor = Color.DimGray };
    private readonly TextBox _logBox = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill };
    private readonly CheckBox _openSetup = new() { Text = "Abrir tela de ativacao ao concluir", Checked = true };
    private readonly CheckBox _runCleanValidation = new() { Text = "Executar validacao de instalacao ao concluir", Checked = true };
    private readonly TextBox _evidenceOutput = new() { Text = @".\artifacts\sync-agent-clean-install-evidence.json" };
    private readonly Label _postgresStatus = new() { AutoSize = true, ForeColor = Color.DimGray };

    private int _step;

    public InstallerWizardForm()
    {
        Text = "PDV Local Sync Agent - Instalador";
        Width = 820;
        Height = 620;
        MinimumSize = new Size(760, 540);
        StartPosition = FormStartPosition.CenterScreen;

        var bottom = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 54,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(10)
        };
        bottom.Controls.AddRange([_cancelButton, _nextButton, _backButton]);

        var header = new Panel { Dock = DockStyle.Top, Height = 86, Padding = new Padding(20, 14, 20, 0) };
        header.Controls.Add(_description);
        header.Controls.Add(_title);

        Controls.Add(_content);
        Controls.Add(header);
        Controls.Add(bottom);

        _backButton.Click += (_, _) => MoveStep(-1);
        _nextButton.Click += async (_, _) => await NextAsync();
        _cancelButton.Click += (_, _) => Close();
        _enableArpa.CheckedChanged += (_, _) => Render();
        _useExistingPostgres.CheckedChanged += (_, _) => Render();
        _installPostgres.CheckedChanged += (_, _) => Render();
        _prepareArpaViews.Click += async (_, _) => await PrepareArpaViewsAsync();
        _createArpaUser.Click += async (_, _) => await CreateArpaReadonlyUserAsync();
        _testArpaConnection.Click += async (_, _) => await TestArpaConnectionAsync();

        Render();
    }

    private static TextBox PasswordBox(string text = "")
    {
        return new TextBox { Text = text, UseSystemPasswordChar = true };
    }

    private void MoveStep(int delta)
    {
        _step = Math.Clamp(_step + delta, 0, 5);
        Render();
    }

    private async Task NextAsync()
    {
        if (_step == 1 && _useExistingPostgres.Checked)
        {
            var valid = await ValidateExistingPostgresAsync(showSuccess: true);
            if (!valid)
            {
                return;
            }
        }

        if (_step < 4)
        {
            MoveStep(1);
            return;
        }

        if (_step == 4)
        {
            if (await InstallAsync())
            {
                _step = 5;
                Render();
            }
            return;
        }

        if (_openSetup.Checked)
        {
            Process.Start(new ProcessStartInfo("http://127.0.0.1:47891/setup") { UseShellExecute = true });
        }

        Close();
    }

    private void Render()
    {
        _content.Controls.Clear();
        _backButton.Enabled = _step > 0 && _step < 5;
        _nextButton.Text = _step switch
        {
            4 => "Instalar",
            5 => "Concluir",
            _ => "Avancar"
        };

        switch (_step)
        {
            case 0:
                RenderWelcome();
                break;
            case 1:
                RenderDatabase();
                break;
            case 2:
                RenderArpa();
                break;
            case 3:
                RenderReview();
                break;
            case 4:
                RenderInstall();
                break;
            default:
                RenderDone();
                break;
        }
    }

    private void SetHeader(string title, string description)
    {
        _title.Text = title;
        _description.Text = description;
    }

    private void RenderWelcome()
    {
        SetHeader("Bem-vindo", "Este assistente instala o PDV Local, SyncAgent e deixa a ativacao do ERP para o dashboard local.");
        var admin = IsAdministrator();
        var text = new Label
        {
            Dock = DockStyle.Top,
            AutoSize = false,
            Height = 180,
            Text = admin
                ? "Permissao de Administrador detectada.\r\n\r\nO instalador vai preparar o banco local, copiar o PDV App e o SyncAgent, instalar o servico Windows, configurar o Tray e criar atalhos do PDV."
                : "Abra este instalador como Administrador.\r\n\r\nSem elevacao, o servico Windows e o bootstrap do banco nao podem ser instalados.",
            Font = new Font("Segoe UI", 11)
        };

        _nextButton.Enabled = admin;
        _content.Controls.Add(text);
    }

    private void RenderDatabase()
    {
        SetHeader("PostgreSQL", "Use PostgreSQL 17 existente ou instale automaticamente a partir dos binarios empacotados.");
        _nextButton.Enabled = true;
        _postgresStatus.Text = "";
        _postgresStatus.Dock = DockStyle.Top;

        var modePanel = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, FlowDirection = FlowDirection.TopDown };
        modePanel.Controls.Add(_useExistingPostgres);
        modePanel.Controls.Add(_installPostgres);

        var grid = NewGrid();
        AddRow(grid, "Pasta de instalacao", _installRoot);
        AddRow(grid, "psql.exe", _psqlPath);
        AddRow(grid, "PostgreSQL host", _postgresHost);
        AddRow(grid, "PostgreSQL porta", _postgresPort);
        AddRow(grid, "Usuario admin PostgreSQL", _postgresAdminUser);
        AddRow(grid, "Senha admin PostgreSQL", _postgresAdminPassword);
        AddRow(grid, "InstallRoot PostgreSQL", _postgresInstallRoot);
        AddRow(grid, "DataDirectory PostgreSQL", _postgresDataDirectory);
        AddRow(grid, "SourceRoot PostgreSQL", _postgresSourceRoot);
        AddRow(grid, "SourceRoot pgvector", _pgVectorSourceRoot);
        AddRow(grid, "Servico PostgreSQL", _postgresServiceName);
        AddRow(grid, "Banco local", _databaseName);
        AddRow(grid, "Usuario local", _databaseUser);
        AddRow(grid, "Senha usuario local", _databasePassword);
        _postgresInstallRoot.Enabled = _installPostgres.Checked;
        _postgresDataDirectory.Enabled = _installPostgres.Checked;
        _postgresSourceRoot.Enabled = _installPostgres.Checked;
        _pgVectorSourceRoot.Enabled = _installPostgres.Checked;
        _postgresServiceName.Enabled = _installPostgres.Checked;
        _psqlPath.Enabled = _useExistingPostgres.Checked;

        _content.Controls.Add(_postgresStatus);
        _content.Controls.Add(grid);
        _content.Controls.Add(modePanel);
    }

    private void RenderArpa()
    {
        SetHeader("Coletor Arpa", "Opcional. O SyncAgent nunca grava no Arpa; usa apenas usuario read-only.");
        _nextButton.Enabled = true;
        var panel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true
        };
        _enableArpa.Dock = DockStyle.Top;
        panel.Controls.Add(_enableArpa);

        var grid = NewGrid();
        grid.Enabled = _enableArpa.Checked;
        AddRow(grid, "Arpa host", _arpaHost);
        AddRow(grid, "Arpa porta", _arpaPort);
        AddRow(grid, "Arpa database", _arpaDatabase);
        AddRow(grid, "Arpa username", _arpaUsername);
        AddRow(grid, "Senha read-only Arpa", _arpaPassword);
        AddRow(grid, "Batch size", _arpaBatchSize);

        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            Padding = new Padding(220, 8, 0, 0)
        };
        _prepareArpaViews.Enabled = _enableArpa.Checked;
        _createArpaUser.Enabled = _enableArpa.Checked;
        _testArpaConnection.Enabled = _enableArpa.Checked;
        _arpaStatus.Text = "";
        _arpaStatus.Padding = new Padding(8, 6, 0, 0);
        actions.Controls.Add(_prepareArpaViews);
        actions.Controls.Add(_createArpaUser);
        actions.Controls.Add(_testArpaConnection);
        actions.Controls.Add(_arpaStatus);

        panel.Controls.Add(grid);
        panel.Controls.Add(actions);
        _content.Controls.Add(panel);
    }

    private void RenderReview()
    {
        SetHeader("Revisao", "Confira as opcoes antes de instalar.");
        _nextButton.Enabled = true;
        var panel = new Panel { Dock = DockStyle.Fill };
        var validationGrid = NewGrid();
        validationGrid.Dock = DockStyle.Top;
        _runCleanValidation.Dock = DockStyle.Top;
        AddRow(validationGrid, "Arquivo de evidencia", _evidenceOutput);
        var summary = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            Dock = DockStyle.Fill,
            ScrollBars = ScrollBars.Vertical,
            Text = BuildSummary()
        };
        panel.Controls.Add(summary);
        panel.Controls.Add(validationGrid);
        panel.Controls.Add(_runCleanValidation);
        _content.Controls.Add(panel);
    }

    private void RenderInstall()
    {
        SetHeader("Instalacao", "Clique em Instalar para executar o instalador local.");
        _nextButton.Enabled = true;
        _logBox.Text = "";
        _content.Controls.Add(_logBox);
    }

    private void RenderDone()
    {
        SetHeader("Concluido", "A instalacao foi executada. Proxima etapa: ativar a conexao com o ERP.");
        _nextButton.Enabled = true;
        var panel = new FlowLayoutPanel { Dock = DockStyle.Top, FlowDirection = FlowDirection.TopDown, AutoSize = true };
        panel.Controls.Add(new Label
        {
            AutoSize = true,
            Text = "Abra o dashboard de ativacao e informe a URL do ERP e o codigo de ativacao.",
            Font = new Font("Segoe UI", 10)
        });
        panel.Controls.Add(_openSetup);
        _content.Controls.Add(panel);
    }

    private static TableLayoutPanel NewGrid()
    {
        return new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 2,
            Padding = new Padding(0, 6, 0, 0)
        };
    }

    private static void AddRow(TableLayoutPanel grid, string label, Control control)
    {
        var row = grid.RowCount++;
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        grid.ColumnStyles.Clear();
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 220));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        control.Width = 480;
        control.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        grid.Controls.Add(new Label { Text = label, AutoSize = true, Padding = new Padding(0, 6, 8, 6) }, 0, row);
        grid.Controls.Add(control, 1, row);
    }

    private string BuildSummary()
    {
        var builder = new StringBuilder();
        builder.AppendLine($"Pasta: {_installRoot.Text}");
        builder.AppendLine($"PostgreSQL: {_postgresHost.Text}:{_postgresPort.Value}");
        builder.AppendLine($"Modo PostgreSQL: {(_installPostgres.Checked ? "instalar automaticamente" : "usar existente")}");
        if (_installPostgres.Checked)
        {
            builder.AppendLine($"PostgreSQL InstallRoot: {_postgresInstallRoot.Text}");
            builder.AppendLine($"PostgreSQL DataDirectory: {_postgresDataDirectory.Text}");
            builder.AppendLine($"PostgreSQL ServiceName: {_postgresServiceName.Text}");
        }
        builder.AppendLine($"Banco local: {_databaseName.Text}");
        builder.AppendLine($"Usuario local: {_databaseUser.Text}");
        builder.AppendLine("Modo ERP: ativacao pos-instalacao pelo dashboard /setup");
        builder.AppendLine($"Validacao pos-instalacao: {(_runCleanValidation.Checked ? "executar" : "nao executar")}");
        if (_runCleanValidation.Checked)
        {
            builder.AppendLine($"Evidencia: {_evidenceOutput.Text}");
        }
        builder.AppendLine($"Coletor Arpa: {(_enableArpa.Checked ? "habilitado" : "desabilitado")}");
        if (_enableArpa.Checked)
        {
            builder.AppendLine($"Arpa connection string: {BuildArpaConnectionString()}");
            builder.AppendLine($"Arpa batch size: {_arpaBatchSize.Value}");
        }
        builder.AppendLine();
        builder.AppendLine("Segredos nao serao gravados em appsettings.json. A senha Arpa, quando usada, sera protegida por DPAPI LocalMachine.");
        return builder.ToString();
    }

    private string BuildArpaConnectionString()
    {
        return string.Join(";", [
            "Host=" + _arpaHost.Text.Trim(),
            "Port=" + _arpaPort.Value,
            "Database=" + _arpaDatabase.Text.Trim(),
            "Username=" + _arpaUsername.Text.Trim()
        ]);
    }

    private async Task TestArpaConnectionAsync()
    {
        if (!_enableArpa.Checked)
        {
            return;
        }

        _testArpaConnection.Enabled = false;
        _nextButton.Enabled = false;
        _arpaStatus.ForeColor = Color.DimGray;
        _arpaStatus.Text = "Testando...";

        try
        {
            if (string.IsNullOrWhiteSpace(_arpaHost.Text)
                || string.IsNullOrWhiteSpace(_arpaDatabase.Text)
                || string.IsNullOrWhiteSpace(_arpaUsername.Text))
            {
                throw new InvalidOperationException("Informe host, database e username do Arpa.");
            }

            if (string.IsNullOrWhiteSpace(_arpaPassword.Text))
            {
                throw new InvalidOperationException("Informe a senha read-only do Arpa para testar a conexao.");
            }

            await EnsureVcRuntimeInstalledAsync();
            var psqlPath = ResolvePsqlForArpaTest();
            var env = new Dictionary<string, string?>
            {
                ["PGHOST"] = _arpaHost.Text.Trim(),
                ["PGPORT"] = _arpaPort.Value.ToString(),
                ["PGDATABASE"] = _arpaDatabase.Text.Trim(),
                ["PGUSER"] = _arpaUsername.Text.Trim(),
                ["PGPASSWORD"] = _arpaPassword.Text,
                ["PGCONNECT_TIMEOUT"] = "10"
            };

            var result = await RunProcessCaptureAsync(
                psqlPath,
                ["-X", "-q", "-t", "-A", "-v", "ON_ERROR_STOP=1", "-c", "SELECT current_database() || ' / ' || current_user;"],
                env);

            _arpaStatus.ForeColor = Color.DarkGreen;
            _arpaStatus.Text = "Conexao OK: " + result.Trim();
            MessageBox.Show("Conexao com Arpa validada com sucesso.", "Coletor Arpa", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            _arpaStatus.ForeColor = Color.DarkRed;
            _arpaStatus.Text = "Falha no teste.";
            MessageBox.Show(ex.Message, "Coletor Arpa", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _testArpaConnection.Enabled = _enableArpa.Checked;
            _nextButton.Enabled = true;
        }
    }

    private async Task CreateArpaReadonlyUserAsync()
    {
        if (!_enableArpa.Checked)
        {
            return;
        }

        _createArpaUser.Enabled = false;
        _testArpaConnection.Enabled = false;
        _nextButton.Enabled = false;
        _arpaStatus.ForeColor = Color.DimGray;
        _arpaStatus.Text = "Criando usuario...";

        try
        {
            if (string.IsNullOrWhiteSpace(_arpaHost.Text)
                || string.IsNullOrWhiteSpace(_arpaDatabase.Text)
                || string.IsNullOrWhiteSpace(_arpaUsername.Text))
            {
                throw new InvalidOperationException("Informe host, database e username do Arpa.");
            }

            if (string.IsNullOrWhiteSpace(_arpaPassword.Text))
            {
                throw new InvalidOperationException("Informe a senha que sera definida para o usuario read-only do Arpa.");
            }

            var confirm = MessageBox.Show(
                "Esta acao conecta no banco Arpa como postgres sem senha e cria/altera o usuario read-only informado. Se as views sync_export.produtos e sync_export.clientes existirem, ela concede apenas SELECT nelas. Continuar?",
                "Criar usuario Arpa",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning);

            if (confirm != DialogResult.Yes)
            {
                _arpaStatus.Text = "Criacao cancelada.";
                return;
            }

            await EnsureVcRuntimeInstalledAsync();
            var psqlPath = ResolvePsqlForArpaTest();
            var env = new Dictionary<string, string?>
            {
                ["PGHOST"] = _arpaHost.Text.Trim(),
                ["PGPORT"] = _arpaPort.Value.ToString(),
                ["PGDATABASE"] = _arpaDatabase.Text.Trim(),
                ["PGUSER"] = "postgres",
                ["PGPASSWORD"] = null,
                ["PGCONNECT_TIMEOUT"] = "10"
            };

            await RunProcessCaptureAsync(
                psqlPath,
                ["-X", "-q", "-v", "ON_ERROR_STOP=1", "-c", BuildCreateArpaReadonlyUserSql()],
                env);

            _arpaStatus.ForeColor = Color.DarkGreen;
            _arpaStatus.Text = "Usuario read-only criado/ajustado.";
            MessageBox.Show("Usuario read-only do Arpa criado/ajustado com sucesso.", "Criar usuario Arpa", MessageBoxButtons.OK, MessageBoxIcon.Information);

            await TestArpaConnectionAsync();
        }
        catch (Exception ex)
        {
            _arpaStatus.ForeColor = Color.DarkRed;
            _arpaStatus.Text = "Falha ao criar usuario.";
            MessageBox.Show(ex.Message, "Criar usuario Arpa", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _createArpaUser.Enabled = _enableArpa.Checked;
            _testArpaConnection.Enabled = _enableArpa.Checked;
            _nextButton.Enabled = true;
        }
    }

    private async Task PrepareArpaViewsAsync()
    {
        if (!_enableArpa.Checked)
        {
            return;
        }

        SetArpaActionButtonsEnabled(false);
        _nextButton.Enabled = false;
        _arpaStatus.ForeColor = Color.DimGray;
        _arpaStatus.Text = "Preparando views...";

        try
        {
            ValidateArpaConnectionFields(requirePassword: true);

            var confirm = MessageBox.Show(
                "Esta acao conecta no banco Arpa como postgres sem senha, cria/atualiza as views sync_export aprovadas, cria/ajusta o usuario read-only e testa a conexao. Continuar?",
                "Preparar views Arpa",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning);

            if (confirm != DialogResult.Yes)
            {
                _arpaStatus.Text = "Preparacao cancelada.";
                return;
            }

            await EnsureVcRuntimeInstalledAsync();
            var psqlPath = ResolvePsqlForArpaTest();
            var env = BuildArpaAdminEnvironment();
            var sqlFile = ResolveArpaViewsSqlFile();

            await RunProcessCaptureAsync(
                psqlPath,
                ["-X", "-q", "-v", "ON_ERROR_STOP=1", "-f", sqlFile],
                env);

            await RunProcessCaptureAsync(
                psqlPath,
                ["-X", "-q", "-v", "ON_ERROR_STOP=1", "-c", BuildCreateArpaReadonlyUserSql()],
                env);

            _arpaStatus.ForeColor = Color.DarkGreen;
            _arpaStatus.Text = "Views e usuario read-only preparados.";
            MessageBox.Show("Views Arpa e usuario read-only preparados com sucesso.", "Preparar views Arpa", MessageBoxButtons.OK, MessageBoxIcon.Information);

            await TestArpaConnectionAsync();
        }
        catch (Exception ex)
        {
            _arpaStatus.ForeColor = Color.DarkRed;
            _arpaStatus.Text = "Falha ao preparar views.";
            MessageBox.Show(ex.Message, "Preparar views Arpa", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            SetArpaActionButtonsEnabled(_enableArpa.Checked);
            _nextButton.Enabled = true;
        }
    }

    private void SetArpaActionButtonsEnabled(bool enabled)
    {
        _prepareArpaViews.Enabled = enabled;
        _createArpaUser.Enabled = enabled;
        _testArpaConnection.Enabled = enabled;
    }

    private void ValidateArpaConnectionFields(bool requirePassword)
    {
        if (string.IsNullOrWhiteSpace(_arpaHost.Text)
            || string.IsNullOrWhiteSpace(_arpaDatabase.Text)
            || string.IsNullOrWhiteSpace(_arpaUsername.Text))
        {
            throw new InvalidOperationException("Informe host, database e username do Arpa.");
        }

        if (requirePassword && string.IsNullOrWhiteSpace(_arpaPassword.Text))
        {
            throw new InvalidOperationException("Informe a senha read-only do Arpa.");
        }
    }

    private Dictionary<string, string?> BuildArpaAdminEnvironment()
    {
        return new Dictionary<string, string?>
        {
            ["PGHOST"] = _arpaHost.Text.Trim(),
            ["PGPORT"] = _arpaPort.Value.ToString(),
            ["PGDATABASE"] = _arpaDatabase.Text.Trim(),
            ["PGUSER"] = "postgres",
            ["PGPASSWORD"] = null,
            ["PGCONNECT_TIMEOUT"] = "10"
        };
    }

    private string ResolveArpaViewsSqlFile()
    {
        var baseDir = AppContext.BaseDirectory;
        var candidates = new[]
        {
            Path.GetFullPath(Path.Combine(baseDir, "..", "..", "infra", "arpa", "sync-export-views-anapolis.initial-load.sql")),
            Path.GetFullPath(Path.Combine(baseDir, "..", "infra", "arpa", "sync-export-views-anapolis.initial-load.sql")),
            Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "infra", "arpa", "sync-export-views-anapolis.initial-load.sql")),
            Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "artifacts", "sync-agent-installer", "infra", "arpa", "sync-export-views-anapolis.initial-load.sql"))
        };

        var sqlFile = candidates.FirstOrDefault(File.Exists);
        if (sqlFile is null)
        {
            throw new FileNotFoundException("SQL de views Arpa nao encontrado no pacote: sync-export-views-anapolis.initial-load.sql");
        }

        return sqlFile;
    }

    private string BuildCreateArpaReadonlyUserSql()
    {
        var runtimeUserLiteral = QuoteSqlLiteral(_arpaUsername.Text.Trim());
        var runtimePasswordLiteral = QuoteSqlLiteral(_arpaPassword.Text);
        var runtimeUserIdentifier = QuoteSqlIdentifier(_arpaUsername.Text.Trim());
        var databaseIdentifier = QuoteSqlIdentifier(_arpaDatabase.Text.Trim());

        return $$"""
DO $$
DECLARE
    runtime_user text := {{runtimeUserLiteral}};
    runtime_password text := {{runtimePasswordLiteral}};
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = runtime_user) THEN
        EXECUTE format('CREATE ROLE %I LOGIN PASSWORD %L NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION', runtime_user, runtime_password);
    ELSE
        EXECUTE format('ALTER ROLE %I WITH LOGIN PASSWORD %L NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION', runtime_user, runtime_password);
    END IF;
END
$$;

GRANT CONNECT ON DATABASE {{databaseIdentifier}} TO {{runtimeUserIdentifier}};
REVOKE CREATE ON SCHEMA public FROM {{runtimeUserIdentifier}};
REVOKE ALL PRIVILEGES ON ALL TABLES IN SCHEMA public FROM {{runtimeUserIdentifier}};

DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM information_schema.schemata WHERE schema_name = 'sync_export') THEN
        GRANT USAGE ON SCHEMA sync_export TO {{runtimeUserIdentifier}};
        REVOKE CREATE ON SCHEMA sync_export FROM {{runtimeUserIdentifier}};
    END IF;

    IF to_regclass('sync_export.produtos') IS NOT NULL THEN
        GRANT SELECT ON sync_export.produtos TO {{runtimeUserIdentifier}};
        REVOKE INSERT, UPDATE, DELETE, TRUNCATE, REFERENCES, TRIGGER ON sync_export.produtos FROM {{runtimeUserIdentifier}};
    END IF;

    IF to_regclass('sync_export.clientes') IS NOT NULL THEN
        GRANT SELECT ON sync_export.clientes TO {{runtimeUserIdentifier}};
        REVOKE INSERT, UPDATE, DELETE, TRUNCATE, REFERENCES, TRIGGER ON sync_export.clientes FROM {{runtimeUserIdentifier}};
    END IF;
END
$$;
""";
    }

    private string ResolvePsqlForArpaTest()
    {
        var candidates = new List<string>();

        if (_useExistingPostgres.Checked && !string.IsNullOrWhiteSpace(_psqlPath.Text))
        {
            candidates.Add(_psqlPath.Text);
        }

        candidates.Add(Path.Combine(_postgresInstallRoot.Text, "bin", "psql.exe"));

        var baseDir = AppContext.BaseDirectory;
        candidates.Add(Path.GetFullPath(Path.Combine(baseDir, "..", "..", "payload", "PostgreSQL17", "pgsql", "bin", "psql.exe")));
        candidates.Add(Path.GetFullPath(Path.Combine(baseDir, "..", "payload", "PostgreSQL17", "pgsql", "bin", "psql.exe")));
        candidates.Add(Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "payload", "PostgreSQL17", "pgsql", "bin", "psql.exe")));
        candidates.Add(Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "artifacts", "sync-agent-installer", "payload", "PostgreSQL17", "pgsql", "bin", "psql.exe")));

        foreach (var candidate in candidates.Where(static value => !string.IsNullOrWhiteSpace(value)))
        {
            if (File.Exists(candidate)
                || (!Path.IsPathFullyQualified(candidate)
                    && !candidate.Contains(Path.DirectorySeparatorChar)
                    && !candidate.Contains(Path.AltDirectorySeparatorChar)))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException("psql.exe nao encontrado para testar o Arpa. Informe o caminho do psql.exe na etapa PostgreSQL ou use o pacote completo com PostgreSQL 17.");
    }

    private async Task EnsureVcRuntimeInstalledAsync()
    {
        await RunProcessCaptureAsync(
            "powershell.exe",
            ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", ResolveVcRuntimeInstallScript()],
            null);
    }

    private async Task<bool> InstallAsync()
    {
        _nextButton.Enabled = false;
        _backButton.Enabled = false;
        _logBox.Text = "Iniciando instalacao...\r\n";

        var script = ResolveInstallScript();
        var vcRuntimeInstallScript = ResolveVcRuntimeInstallScript();
        var dotNetRuntimeInstallScript = ResolveDotNetRuntimeInstallScript();
        var postgresInstallScript = _installPostgres.Checked ? ResolvePostgresInstallScript() : "";
        var installScript = BuildInstallScript(script, vcRuntimeInstallScript, dotNetRuntimeInstallScript, postgresInstallScript);

        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = "-NoProfile -ExecutionPolicy Bypass -Command -",
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => AppendLog(e.Data);
        process.ErrorDataReceived += (_, e) => AppendLog(e.Data);
        process.Start();
        await process.StandardInput.WriteLineAsync(installScript);
        await process.StandardInput.FlushAsync();
        process.StandardInput.Close();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await process.WaitForExitAsync();

        if (process.ExitCode != 0)
        {
            _nextButton.Enabled = true;
            _backButton.Enabled = true;
            AppendLog($"Instalacao falhou. ExitCode={process.ExitCode}");
            MessageBox.Show("A instalacao falhou. Verifique o log exibido.", "Instalacao", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }

        AppendLog("Instalacao concluida.");

        if (_runCleanValidation.Checked)
        {
            AppendLog("Iniciando validacao de instalacao...");
            var validationExitCode = await RunPowerShellScriptAsync(BuildValidationScript(ResolveValidationScript()));
            if (validationExitCode != 0)
            {
                _nextButton.Enabled = true;
                _backButton.Enabled = true;
                AppendLog($"Validacao falhou. ExitCode={validationExitCode}");
                MessageBox.Show("A instalacao foi executada, mas a validacao falhou. Verifique o log e o arquivo de evidencia.", "Validacao", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }

            AppendLog("Validacao concluida.");
        }

        _nextButton.Enabled = true;
        return true;
    }

    private string ResolveInstallScript()
    {
        var baseDir = AppContext.BaseDirectory;
        var candidates = new[]
        {
            Path.GetFullPath(Path.Combine(baseDir, "..", "..", "infra", "install-sync-agent.ps1")),
            Path.GetFullPath(Path.Combine(baseDir, "..", "infra", "install-sync-agent.ps1")),
            Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "infra", "install-sync-agent.ps1"))
        };

        var script = candidates.FirstOrDefault(File.Exists);
        if (script is null)
        {
            throw new FileNotFoundException("install-sync-agent.ps1 nao encontrado no pacote.");
        }

        return script;
    }

    private string ResolvePostgresInstallScript()
    {
        var baseDir = AppContext.BaseDirectory;
        var candidates = new[]
        {
            Path.GetFullPath(Path.Combine(baseDir, "..", "..", "infra", "install-postgresql17-local.ps1")),
            Path.GetFullPath(Path.Combine(baseDir, "..", "infra", "install-postgresql17-local.ps1")),
            Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "infra", "install-postgresql17-local.ps1"))
        };

        var script = candidates.FirstOrDefault(File.Exists);
        if (script is null)
        {
            throw new FileNotFoundException("install-postgresql17-local.ps1 nao encontrado no pacote.");
        }

        return script;
    }

    private string ResolveVcRuntimeInstallScript()
    {
        var baseDir = AppContext.BaseDirectory;
        var candidates = new[]
        {
            Path.GetFullPath(Path.Combine(baseDir, "..", "..", "infra", "install-vc-redist-x64.ps1")),
            Path.GetFullPath(Path.Combine(baseDir, "..", "infra", "install-vc-redist-x64.ps1")),
            Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "infra", "install-vc-redist-x64.ps1")),
            Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "artifacts", "sync-agent-installer", "infra", "install-vc-redist-x64.ps1"))
        };

        var script = candidates.FirstOrDefault(File.Exists);
        if (script is null)
        {
            throw new FileNotFoundException("install-vc-redist-x64.ps1 nao encontrado no pacote.");
        }

        return script;
    }

    private string ResolveDotNetRuntimeInstallScript()
    {
        var baseDir = AppContext.BaseDirectory;
        var candidates = new[]
        {
            Path.GetFullPath(Path.Combine(baseDir, "..", "..", "infra", "install-dotnet8-desktop-runtime.ps1")),
            Path.GetFullPath(Path.Combine(baseDir, "..", "infra", "install-dotnet8-desktop-runtime.ps1")),
            Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "infra", "install-dotnet8-desktop-runtime.ps1"))
        };

        var script = candidates.FirstOrDefault(File.Exists);
        if (script is null)
        {
            throw new FileNotFoundException("install-dotnet8-desktop-runtime.ps1 nao encontrado no pacote.");
        }

        return script;
    }

    private string ResolveValidationScript()
    {
        var baseDir = AppContext.BaseDirectory;
        var candidates = new[]
        {
            Path.GetFullPath(Path.Combine(baseDir, "..", "..", "infra", "test-sync-agent-clean-install.ps1")),
            Path.GetFullPath(Path.Combine(baseDir, "..", "infra", "test-sync-agent-clean-install.ps1")),
            Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "infra", "test-sync-agent-clean-install.ps1"))
        };

        var script = candidates.FirstOrDefault(File.Exists);
        if (script is null)
        {
            throw new FileNotFoundException("test-sync-agent-clean-install.ps1 nao encontrado no pacote.");
        }

        return script;
    }

    private string BuildInstallScript(string script, string vcRuntimeInstallScript, string dotNetRuntimeInstallScript, string postgresInstallScript)
    {
        var psqlPath = _psqlPath.Text;
        var lines = new List<string>
        {
            "$ErrorActionPreference = 'Stop'",
            "trap { Write-Error $_; exit 1 }"
        };

        lines.Add(string.Join(" ", [
            "&",
            Quote(vcRuntimeInstallScript)
        ]));

        lines.Add(string.Join(" ", [
            "&",
            Quote(dotNetRuntimeInstallScript),
            "-MinimumMajorVersion", "8"
        ]));

        if (_installPostgres.Checked)
        {
            psqlPath = Path.Combine(_postgresInstallRoot.Text, "bin", "psql.exe");
            var postgresArgs = new List<string>
            {
                "&",
                Quote(postgresInstallScript),
                "-InstallRoot", Quote(_postgresInstallRoot.Text),
                "-DataDirectory", Quote(_postgresDataDirectory.Text),
                "-ServiceName", Quote(_postgresServiceName.Text),
                "-Port", _postgresPort.Value.ToString(),
            "-Superuser", Quote(_postgresAdminUser.Text),
            "-SuperuserPassword", $"(ConvertTo-SecureString {Quote(_postgresAdminPassword.Text)} -AsPlainText -Force)"
        };

        if (!string.IsNullOrWhiteSpace(_postgresSourceRoot.Text))
        {
            postgresArgs.AddRange(["-SourceRoot", Quote(_postgresSourceRoot.Text)]);
        }

        if (!string.IsNullOrWhiteSpace(_pgVectorSourceRoot.Text))
        {
            postgresArgs.AddRange(["-PgVectorSourceRoot", Quote(_pgVectorSourceRoot.Text)]);
        }

        lines.Add(string.Join(" ", postgresArgs));
        }

        var args = new List<string>
        {
            "&",
            Quote(script),
            "-InstallRoot", Quote(_installRoot.Text),
            "-EnablePostInstallActivation",
            "-PsqlPath", Quote(psqlPath),
            "-PostgresHost", Quote(_postgresHost.Text),
            "-PostgresPort", _postgresPort.Value.ToString(),
            "-PostgresAdminUser", Quote(_postgresAdminUser.Text),
            "-PostgresAdminPassword", $"(ConvertTo-SecureString {Quote(_postgresAdminPassword.Text)} -AsPlainText -Force)",
            "-DatabaseName", Quote(_databaseName.Text),
            "-DatabaseUser", Quote(_databaseUser.Text),
            "-DatabasePassword", Quote(_databasePassword.Text)
        };

        if (_enableArpa.Checked)
        {
            args.AddRange([
                "-EnableArpaCollector",
                "-ArpaConnectionString", Quote(BuildArpaConnectionString()),
                "-ArpaPassword", $"(ConvertTo-SecureString {Quote(_arpaPassword.Text)} -AsPlainText -Force)",
                "-ArpaCollectorPreset", "AnapolisInitialLoad",
                "-ArpaBatchSize", _arpaBatchSize.Value.ToString()
            ]);
        }

        lines.Add(string.Join(" ", args));
        lines.Add("exit $LASTEXITCODE");
        return string.Join(Environment.NewLine, lines);
    }

    private string BuildValidationScript(string validationScript)
    {
        var psqlPath = _installPostgres.Checked
            ? Path.Combine(_postgresInstallRoot.Text, "bin", "psql.exe")
            : _psqlPath.Text;
        var evidenceOutput = string.IsNullOrWhiteSpace(_evidenceOutput.Text)
            ? @".\artifacts\sync-agent-clean-install-evidence.json"
            : _evidenceOutput.Text;

        var args = new List<string>
        {
            "&",
            Quote(validationScript),
            "-InstallRoot", Quote(_installRoot.Text),
            "-PsqlPath", Quote(psqlPath),
            "-PostgresHost", Quote(_postgresHost.Text),
            "-PostgresPort", _postgresPort.Value.ToString(),
            "-DatabaseName", Quote(_databaseName.Text),
            "-DatabaseUser", Quote(_databaseUser.Text),
            "-DatabasePassword", Quote(_databasePassword.Text),
            "-SkipErp",
            "-EvidenceOutput", Quote(evidenceOutput)
        };

        return "$ErrorActionPreference = 'Stop'" + Environment.NewLine
            + "trap { Write-Error $_; exit 1 }" + Environment.NewLine
            + string.Join(" ", args) + Environment.NewLine
            + "exit $LASTEXITCODE";
    }

    private async Task<int> RunPowerShellScriptAsync(string script)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = "-NoProfile -ExecutionPolicy Bypass -Command -",
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => AppendLog(e.Data);
        process.ErrorDataReceived += (_, e) => AppendLog(e.Data);
        process.Start();
        await process.StandardInput.WriteLineAsync(script);
        await process.StandardInput.FlushAsync();
        process.StandardInput.Close();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await process.WaitForExitAsync();
        return process.ExitCode;
    }

    private async Task<bool> ValidateExistingPostgresAsync(bool showSuccess)
    {
        _nextButton.Enabled = false;
        _postgresStatus.Text = "Validando PostgreSQL 17 e pgvector 0.8.0...";
        try
        {
            await EnsureVcRuntimeInstalledAsync();

            var version = await RunProcessCaptureAsync(_psqlPath.Text, ["--version"], null);
            if (!version.Contains("PostgreSQL) 17.", StringComparison.OrdinalIgnoreCase)
                && !version.Contains("PostgreSQL 17.", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("psql nao e PostgreSQL major 17: " + version);
            }

            var password = _postgresAdminPassword.Text;
            var env = new Dictionary<string, string?>
            {
                ["PGHOST"] = _postgresHost.Text,
                ["PGPORT"] = _postgresPort.Value.ToString(),
                ["PGUSER"] = _postgresAdminUser.Text,
                ["PGPASSWORD"] = password
            };

            var available = await RunProcessCaptureAsync(
                _psqlPath.Text,
                ["-d", "postgres", "-tAc", "SELECT default_version FROM pg_available_extensions WHERE name = 'vector';"],
                env);

            var vectorVersion = available.Trim();
            if (vectorVersion != "0.8.0")
            {
                throw new InvalidOperationException("pgvector 0.8.0 nao esta disponivel. Versao detectada: " + (string.IsNullOrWhiteSpace(vectorVersion) ? "missing" : vectorVersion));
            }

            _postgresStatus.ForeColor = Color.DarkGreen;
            _postgresStatus.Text = "PostgreSQL 17 e pgvector 0.8.0 validados.";
            if (showSuccess)
            {
                MessageBox.Show("PostgreSQL 17 e pgvector 0.8.0 validados.", "PostgreSQL", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            return true;
        }
        catch (Exception ex)
        {
            _postgresStatus.ForeColor = Color.DarkRed;
            _postgresStatus.Text = ex.Message;
            MessageBox.Show(ex.Message, "PostgreSQL", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
        finally
        {
            _nextButton.Enabled = true;
        }
    }

    private static async Task<string> RunProcessCaptureAsync(string fileName, IEnumerable<string> arguments, IDictionary<string, string?>? environment)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        if (environment is not null)
        {
            foreach (var item in environment)
            {
                if (item.Value is null)
                {
                    startInfo.Environment.Remove(item.Key);
                }
                else
                {
                    startInfo.Environment[item.Key] = item.Value;
                }
            }
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Nao foi possivel iniciar " + fileName);
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var output = await outputTask;
        var error = await errorTask;

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? output : error);
        }

        return output;
    }

    private void AppendLog(string? line)
    {
        if (string.IsNullOrEmpty(line))
        {
            return;
        }

        if (InvokeRequired)
        {
            BeginInvoke(() => AppendLog(line));
            return;
        }

        _logBox.AppendText(line + Environment.NewLine);
    }

    private static string Quote(string value)
    {
        return "'" + value.Replace("'", "''") + "'";
    }

    private static string QuoteSqlLiteral(string value)
    {
        return "'" + value.Replace("'", "''") + "'";
    }

    private static string QuoteSqlIdentifier(string value)
    {
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }

    private static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }
}


