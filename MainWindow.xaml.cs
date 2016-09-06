using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace RPGenerator
{
    public class AgentDescriptor
    {
        public bool ShouldUse { get; set; }

        public string Name { get; set; }
    }

    /// <summary>
    ///     Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window, INotifyPropertyChanged
    {
        private const string CmdUtilPath = @"C:\Program Files\AppRecovery\Core\CoreService\cmdutil.exe";

        private CancellationTokenSource _cancellationTokenSource;
        private string _coreHost;
        private string _coreUser;
        private string _corePassword;
        private DateTime _startTime;
        private DateTime _endTime;
        private int _interval;

        private Task _generationTask;

        public MainWindow()
        {
            InitializeComponent();
            AgentsCollection = new ObservableCollection<AgentDescriptor>();
            Agents.ItemsSource = AgentsCollection;
        }

        #region PropertyChanged
        public event PropertyChangedEventHandler PropertyChanged;

        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
        #endregion

        public ObservableCollection<AgentDescriptor> AgentsCollection { get; set; }

        private void StartGeneration(object sender, RoutedEventArgs e)
        {
            if (AgentsCollection.All(x => !x.ShouldUse))
            {
                MessageBox.Show("Please select at least one agent.");
                return;
            }

            if (Start.SelectedDate == null || End.SelectedDate == null)
            {
                MessageBox.Show("You have to select Start and End date");
                return;
            }

            if (Start.SelectedDate > End.SelectedDate || Start.SelectedDate == End.SelectedDate)
            {
                MessageBox.Show("Start date should be less then End date.");
                return;
            }
            
            if (string.IsNullOrEmpty(Interval.Text) || !int.TryParse(Interval.Text, out _interval))
            {
                MessageBox.Show("Interval filed should be not blank and should contain only numbers.");
                return;
            }

            _cancellationTokenSource = new CancellationTokenSource();
            _generationTask = Task.Factory.StartNew(() => GenerateData(_cancellationTokenSource.Token));
        }

        private void ConnectToCore(object sender, RoutedEventArgs e)
        {
            _coreHost = string.Empty;
            _coreUser = string.Empty;
            _corePassword = string.Empty;
            if (string.IsNullOrEmpty(Core.Text) || string.IsNullOrEmpty(User.Text) || string.IsNullOrEmpty(Password.Password))
            {
                MessageBox.Show("Please fill core, user and password fields.");
                return;
            }

            if (!RunCmdUtil($"-list repositories -core {Core.Text} -user {User.Text} -password {Password.Password}",
                    p =>
                    {
                        var error = p.StandardError.ReadToEnd()
                            .Replace("\0", string.Empty)
                            .Replace("\r\n", Environment.NewLine);
                        if (error.Length > 0)
                        {
                            MessageBox.Show(error);
                            return false;
                        }

                        return true;
                    }))
            {
                return;
            }
            
            Dispatcher.Invoke(() => _coreUser = User.Text);
            Dispatcher.Invoke(() => _corePassword = Password.Password);
            Dispatcher.Invoke(() => _coreHost = Core.Text);
            if (!RefreshAgents())
            {
                return;
            }

            Connect.Visibility = Visibility.Collapsed;
            ConnectionSettings.IsEnabled = false;
            Reset.Visibility = Visibility.Visible;
            GenerationSettings.Visibility = Visibility.Visible;
            GenerationStart.IsEnabled = true;
            GenerationStop.IsEnabled = true;
        }

        private void RefreshAgents(object sender, RoutedEventArgs e)
        {
            RefreshAgents();
        }

        private bool RefreshAgents()
        {
            if (!RunCmdUtil($"/list protectedservers -core {_coreHost} -user {_coreUser} -password {_corePassword}",
                            p =>
                            {
                                var error = p.StandardError.ReadToEnd()
                                    .Replace("\0", string.Empty)
                                    .Replace("\r\n", Environment.NewLine);
                                if (error.Length > 0)
                                {
                                    Dispatcher.Invoke(() => MessageBox.Show(error));
                                    return false;
                                }

                                var output = p.StandardOutput.ReadToEnd()
                                              .Replace("\0", string.Empty)
                                              .Replace("\r\n", Environment.NewLine)
                                              .Replace(Environment.NewLine, "\n");
                                var lines = output.Split('\n').Skip(2).ToArray();
                                if (!lines.Any())
                                {
                                    Dispatcher.Invoke(() => MessageBox.Show("Either cannot connect to core, or credentials are incorrect, or core has no protected agents."));
                                    return false;
                                }

                                AgentsCollection.Clear();
                                foreach (var agentName in from entry in lines select entry.Split('|') into parts where parts.Length >= 2 select parts.First().TrimStart(' ').TrimEnd(' '))
                                {
                                    AgentsCollection.Add(new AgentDescriptor
                                    {
                                        Name = agentName,
                                        ShouldUse = false
                                    });
                                }

                                return true;
                            }))
            {
                return false;
            }

            OnPropertyChanged("AgentsCollection");
            return true;
        }

        private void GenerateData(CancellationToken token)
        {
            try
            { 
                _startTime = DateTime.Now;
                _endTime = DateTime.Now;
                var intervalString = string.Empty;
                Dispatcher.Invoke(() => _startTime = Start.SelectedDate.Value);
                Dispatcher.Invoke(() => _endTime = End.SelectedDate.Value);
                Dispatcher.Invoke(() => intervalString = Interval.Text);
                _interval = int.Parse(intervalString);
                while (_startTime <= _endTime)
                {
                    foreach (var agent in AgentsCollection)
                    {
                        token.ThrowIfCancellationRequested();
                        var sw = new Stopwatch();
                        sw.Start();
                        Dispatcher.Invoke(() => Output.Text += $"Generating recovery point for date {_startTime} and agent {agent.Name}");
                        Dispatcher.Invoke(() => Output.Text += Environment.NewLine);

                        SetSystemTime(_startTime);

                        Thread.Sleep(5000);

                        ForcePhoneHome(_coreHost, _coreUser, _corePassword);

                        Dispatcher.Invoke(() => Output.Text += "Forcing transfer");
                        Dispatcher.Invoke(() => Output.Text += Environment.NewLine);

                        token.ThrowIfCancellationRequested();
                        if (!RunCmdUtil($"-force -core {_coreHost} -user {_coreUser} -password {_corePassword} -protectedserver {agent.Name}",
                                p =>
                                {
                                    var error = p.StandardError.ReadToEnd()
                                        .Replace("\0", string.Empty)
                                        .Replace("\r\n", Environment.NewLine);
                                    var output = p.StandardOutput.ReadToEnd()
                                                  .Replace("\0", string.Empty)
                                                  .Replace("\r\n", Environment.NewLine);
                                    if (output.Contains("Call to service method"))
                                    {
                                        Dispatcher.Invoke(() => MessageBox.Show($"Call to force snapshot failed. Error is: {output}"));
                                    }

                                    if (error.Length > 0)
                                    {
                                        Dispatcher.Invoke(() => MessageBox.Show(error));
                                        return false;
                                    }

                                    return true;
                                }))
                        {
                            return;
                        }

                        Thread.Sleep(5000);

                        Dispatcher.Invoke(() => Output.Text += "Waiting for transfer to end");
                        Dispatcher.Invoke(() => Output.Text += Environment.NewLine);

                        token.ThrowIfCancellationRequested();
                        while (!RunCmdUtil($"-list activejobs -core {_coreHost} -user {_coreUser} -password {_corePassword} -protectedserver {agent.Name}",
                                p =>
                                {
                                    var output = p.StandardOutput.ReadToEnd()
                                        .Replace("\0", string.Empty)
                                        .Replace("\r\n", Environment.NewLine);
                                    return output.Contains("No jobs of the specified type were found on the core");
                                }))
                        {
                            token.ThrowIfCancellationRequested();
                            Thread.Sleep(100);
                        }

                        sw.Stop();

                        Dispatcher.Invoke(() => Output.Text += $"Generating recovery point took {sw.Elapsed}");
                        Dispatcher.Invoke(() => Output.Text += Environment.NewLine);

                        _startTime = _startTime.AddHours(_interval);
                        if (!RunCmdUtil($"-forcerollup -core {_coreHost} -user {_coreUser} -password {_corePassword} -protectedserver {agent.Name}",
                                p =>
                                {
                                    var error = p.StandardError.ReadToEnd()
                                        .Replace("\0", string.Empty)
                                        .Replace("\r\n", Environment.NewLine);
                                    if (error.Length > 0)
                                    {
                                        Dispatcher.Invoke(() => MessageBox.Show(error));
                                        return false;
                                    }

                                    return true;
                                }))
                        {
                            return;
                        }

                        while (!RunCmdUtil($"-list activejobs -core {_coreHost} -user {_coreUser} -password {_corePassword} -protectedserver {agent.Name}",
                                p =>
                                {
                                    var output = p.StandardOutput.ReadToEnd()
                                        .Replace("\0", string.Empty)
                                        .Replace("\r\n", Environment.NewLine);
                                    return output.Contains("No jobs of the specified type were found on the core");
                                }))
                        {
                            token.ThrowIfCancellationRequested();
                            Thread.Sleep(100);
                        }
                    }
                }

                Dispatcher.Invoke(() => Output.Text += "Finished generation!");
                Dispatcher.Invoke(() => Output.Text += Environment.NewLine);
            }
            catch(OperationCanceledException)
            {
                Dispatcher.Invoke(() => Output.Text += "Generation was cancelled!");
            }
        }
        
        private void StopGeneratrion(object sender, RoutedEventArgs e)
        {
            Dispatcher.Invoke(() => Output.Text += "Cancelling generation!");
            Dispatcher.Invoke(() => Output.Text += Environment.NewLine);
            _generationTask.ContinueWith(x =>
            {
                Dispatcher.Invoke(() =>
                {
                    ControlPanel.IsEnabled = true;
                    Buttons.IsEnabled = true;
                });
            });

            Dispatcher.Invoke(() =>
            {
                ControlPanel.IsEnabled = false;
                Buttons.IsEnabled = false;
            });

            _cancellationTokenSource.Cancel();
        }

        private void ForcePhoneHome(string core, string user, string password)
        {
            try
            {
                var request = WebRequest.Create($"https://{core}:8006/apprecovery/admin/license/phoneHome/force");
                request.Method = "POST";
                const string postData = "Force License check.";
                var byteArray = Encoding.UTF8.GetBytes(postData);
                request.ContentType = "application/x-www-form-urlencoded";
                request.ContentLength = byteArray.Length;
                request.Credentials = new NetworkCredential(user, password);
                var dataStream = request.GetRequestStream();
                dataStream.Write(byteArray, 0, byteArray.Length);
                dataStream.Close();
                var response = request.GetResponse();
                Dispatcher.Invoke(() => Output.Text += "Successfully forced phone home.");
                Dispatcher.Invoke(() => Output.Text += Environment.NewLine);
                dataStream = response.GetResponseStream();
                if (dataStream != null)
                {
                    var reader = new StreamReader(dataStream);
                    reader.ReadToEnd();
                    reader.Close();
                }

                dataStream?.Close();
                response.Close();
            }
            catch (Exception e)
            {
                Dispatcher.Invoke(() => Output.Text += $"Failed to force phone home. Error message: {e.Message}");
                Dispatcher.Invoke(() => Output.Text += Environment.NewLine);
            }
        }
        
        private void ResetConnection(object sender, RoutedEventArgs e)
        {
            _coreHost = string.Empty;
            _coreUser = string.Empty;
            _corePassword = string.Empty;
            User.Text = string.Empty;
            Password.Password = string.Empty;
            Core.Text = string.Empty;
            Connect.Visibility = Visibility.Visible;
            AgentsCollection.Clear();
            GenerationSettings.Visibility = Visibility.Collapsed;
            Reset.Visibility = Visibility.Collapsed;
            GenerationStart.IsEnabled = false;
            GenerationStop.IsEnabled = false;
            ConnectionSettings.IsEnabled = true;
        }

        private void SetSystemTime(DateTime dt)
        {
            var options = new ConnectionOptions
            {
                Username = _coreUser,
                Password = _corePassword
            };

            var path = new ManagementPath($"\\\\{_coreHost}\\root\\cimv2");

            var scope = new ManagementScope(path, options);

            foreach (var o in new ManagementClass(scope, new ManagementPath("Win32_OperatingSystem"), null).GetInstances())
            {
                var classInstance = (ManagementObject) o;
                var inParams = classInstance.GetMethodParameters("SetDateTime");
                inParams["LocalDateTime"] = ManagementDateTimeConverter.ToDmtfDateTime(dt);
                classInstance.InvokeMethod("SetDateTime", inParams, null);
            }
        }

        private static bool RunCmdUtil(string arguments, Func<Process, bool> checFunc)
        {
            var processStartInfo = new ProcessStartInfo
            {
                Arguments = arguments,
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                FileName = CmdUtilPath
            };

            using (var p = Process.Start(processStartInfo))
            {
                if (p == null)
                {
                    return true;
                }

                p.WaitForExit();
                return checFunc == null || checFunc(p);
            }
        }
    }
}