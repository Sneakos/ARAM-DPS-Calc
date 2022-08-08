using Utilities;
using RiotSharp;
using RiotSharp.Misc;
using System.Linq;
using System.Net;
using RiotSharp.Endpoints.MatchEndpoint;
using System.Runtime.InteropServices;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RiotSharp.Endpoints.ChampionMasteryEndpoint;
using RiotSharp.Endpoints.SummonerEndpoint;
using RiotSharp.Endpoints.LeagueEndpoint;
using System.Configuration;

namespace DPS_Calc
{
    public partial class DPSCalc : Form
    {
        private readonly string API_KEY;
        private readonly string USERNAME;
        private readonly RiotSharp.Misc.Region NA_RGN = RiotSharp.Misc.Region.Na;
        private readonly RiotSharp.Misc.Region AM_RGN = RiotSharp.Misc.Region.Americas;
        private readonly string ChampionImageBaseUri = "http://ddragon.leagueoflegends.com/cdn/12.14.1/img/champion/";
        private readonly string ChampionDataBaseUri = "http://ddragon.leagueoflegends.com/cdn/12.14.1/data/en_US/champion/";

#if DEBUG
        private readonly string AssetsFolder = @"..\..\..\Assets\";
#else
        private readonly string AssetsFolder = @"\Assets\";
#endif

        private string ChampionImageFileLocation;
        private string ChampNotFoundImageLocation;

        private RiotApi _api;

        private int _requiredDamage;
        private int _gameLengthSeconds;

        private string _currDetailSummId = "";

        public DPSCalc()
        {
            InitializeComponent();

            API_KEY = GetAppSetting("APIKey");
            USERNAME = GetAppSetting("UserName");

            btnRefresh.Image = DrawingControl.ResizeImage(btnRefresh.Image, btnRefresh.Size);
            ChampionImageFileLocation = AssetsFolder + @"ChampionIcons\";
            ChampNotFoundImageLocation = AssetsFolder + @"ChampionIcons\ChampNotFound.png";

            _api = RiotApi.GetDevelopmentInstance(API_KEY);
            _requiredDamage = 0;
            _gameLengthSeconds = 0;

        }

        #region Events

        private async void btnRefresh_Click(object sender, EventArgs e)
        {
            try
            {
                Summoner summoner = await _api.Summoner.GetSummonerByNameAsync(NA_RGN, USERNAME);

                long unixsec = ((DateTimeOffset)DateTime.Now.AddHours(-2)).ToUnixTimeSeconds();
                List<string> matches = await _api.Match.GetMatchListAsync(AM_RGN, summoner.Puuid, startTime: unixsec, count: 30);

                string mostRecentMatchID = matches.Count > 0 ? matches[0] : "";

                if (string.IsNullOrEmpty(mostRecentMatchID) == false)
                {
                    ProgressBarInfo[] pbars = new ProgressBarInfo[5];

                    Match match = await _api.Match.GetMatchAsync(AM_RGN, mostRecentMatchID);

                    _gameLengthSeconds = GetGameDurationSeconds(match.Info);
                    _requiredDamage = GetRequiredDamage(_gameLengthSeconds);

                    lblGameLength.Text = "Game Length: " + new TimeSpan(0, 0, _gameLengthSeconds).ToString("mm':'ss");

                    int index = 0;

                    Participant mainUser = match.Info.Participants.Where(x => x.SummonerName == USERNAME).First();
                    await AddProgressBarInfo(mainUser, pbars, 0);

                    index++;

                    foreach (Participant participant in match.Info.Participants.Where(x => x.TeamId == mainUser.TeamId && x.ParticipantId != mainUser.ParticipantId))
                    {
                        await AddProgressBarInfo(participant, pbars, index);

                        index++;
                    }

                    for (int i = 0; i < pbars.Length; i++)
                    {
                        Panel pnl = (Panel)pnlDamageDone.Controls["pnlChamp" + i];
                        ProgressBar pbar = (ProgressBar)pnl.Controls["pbarChamp" + i];

                        Button btn = (Button)pnl.Controls["btnChamp" + i];

                        pbar.Maximum = pbars[i].Maximum;
                        pbar.Value = pbars[i].Value;

                        SetProgressBarState(pbar, pbars[i].State);
                        btn.Image = pbars[i].ChampionImage;

                        btn.Click += pnlItem_Click; 
                        pbar.Click += pnlItem_Click;
                        pnl.Click += pnl_Click;

                        pnl.Tag = pbars[i].Participant;
                    }

                    await SetupDetailPanel(mainUser);
                }
            }
            catch (RiotSharpException ex)
            {
                MessageBox.Show(ex.Message);
            }
        }

        private async Task AddProgressBarInfo(Participant participant, ProgressBarInfo[] pbars, int index)
        {
            int damageDealt = (int)participant.TotalDamageDealtToChampions;
            string champPlayed = participant.ChampionName;

            pbars[index] = new ProgressBarInfo
            {
                ChampionImage = await GetChampionImage(champPlayed, btnChamp0.Size),
                Maximum = _requiredDamage,
                Value = damageDealt >= _requiredDamage ? _requiredDamage : damageDealt,
                State = damageDealt >= _requiredDamage ? ProgressBarState.Green : ProgressBarState.Red,
                Participant = participant
            };
        }
        private async void pnl_Click(object? sender, EventArgs e)
        {
            if (sender == null) return;
            if (sender is not Panel pnl) return;
            if (pnl.Tag is not Participant participant) return;

            await SetupDetailPanel(participant);
        }

        private async void pnlItem_Click(object? sender, EventArgs e)
        {
            if (sender == null) return;
            if (sender is not Control ctrl) return;
            if (ctrl.Parent.Tag is not Participant participant) return;
            if (participant.SummonerId == _currDetailSummId) return;

            await SetupDetailPanel(participant);
        }

        #endregion Events

        #region Helpers

        private async Task SetupDetailPanel(Participant participant)
        {
            pnlDetails.Visible = true;
            _currDetailSummId = participant.SummonerId;

            int champID = await GetChampionID(participant.ChampionName);
            ChampionMastery champMastery = await _api.ChampionMastery.GetChampionMasteryAsync(NA_RGN, participant.SummonerId, champID);
            int totalDamgeDealt = (int)participant.TotalDamageDealtToChampions;

            pbDetailChamp.Image = await GetChampionImage(participant.ChampionName, pbDetailChamp.Size);
            lblCurrentlyShowing.Text = "Currently Showing: " + participant.ChampionName + $" ({participant.SummonerName})";

            lblMasteryPoints.Text = $"Mastery Points: {champMastery.ChampionPoints} (Level {champMastery.ChampionLevel})";

            lblTotalDamageRequired.Text = $"Total Damage Required: {_requiredDamage}";

            lblDamageDealtValue.Text = _requiredDamage.ToString();

            int totaldpm = (int)(totalDamgeDealt / (_gameLengthSeconds / 60.0));

            lblTotalDPSValue.Text = totaldpm.ToString();

            List <LeagueEntry> response = await _api.League.GetLeagueEntriesBySummonerAsync(NA_RGN, participant.SummonerId);

            List<LeagueEntry> rankedSoloEntries = response.Where(x => x.QueueType == "RANKED_SOLO_5x5").ToList();
            
            if (rankedSoloEntries.Count == 0)
            {
                lblRank.Text = $"Rank: Unranked";
            }
            else
            {
                LeagueEntry soloEntry = rankedSoloEntries.First();

                string rank = soloEntry.Tier.ToLower() + " " + soloEntry.Rank;

                rank = rank[..1].ToUpper() + rank[1..];

                lblRank.Text = $"Rank: {rank}";
            }

            if (totalDamgeDealt < _requiredDamage)
            {
                double dps = 1800.0;
                double timeReq = totalDamgeDealt / dps;

                int seconds = (int)(timeReq * 60);
                TimeSpan timeNeeded = new TimeSpan(0, 0, seconds);
                TimeSpan timeOver = new TimeSpan(0, 0, _gameLengthSeconds - seconds);

                lblEndBy.Text = $"Needed to end by: ";
                lblEndByValue.Text = $"{timeNeeded.ToString("mm':'ss")} ({timeOver.ToString("mm':'ss")} over)";
                lblOr.Visible = true;
                lblDamage.Text = $"Damage Needed: ";
                lblDamageNeededValue.Text = $"{_requiredDamage - totalDamgeDealt}";
            }
            else
            {
                double dps = 1800.0;
                double timeReq = totalDamgeDealt / dps;

                int seconds = (int)(timeReq * 60);
                TimeSpan timeToSpare = new TimeSpan(0, 0, seconds - _gameLengthSeconds);

                lblEndBy.Text = $"Time to spare: ";
                lblEndByValue.Text = $"{timeToSpare.ToString("mm':'ss")}";
                lblOr.Visible = false;
                lblDamage.Text = $"Damage Surplus: ";
                lblDamageNeededValue.Text = $"{totalDamgeDealt - _requiredDamage}";
            }
        }

        private int GetGameDurationSeconds(MatchInfo matchInfo)
        {
            return (int)matchInfo.Participants.Max(x => x.timePlayed.TotalSeconds);
        }

        private int GetRequiredDamage(int totalSeconds)
        {
            double gameLength = totalSeconds / 60.0;

            int requiredDPS = 1800;

            return (int)Math.Ceiling(gameLength * requiredDPS);
        }

        private async Task<Image> GetChampionImage(string champName)
        {
            return await GetChampionImage(champName, Size.Empty);
        }

        private async Task<Image> GetChampionImage(string champName, Size size)
        {
            string filePath = ChampionImageFileLocation + champName + ".png";
            Image championImage;

            if (File.Exists(filePath))
            {
                championImage = Image.FromFile(filePath);
            }
            else
            {
                string championUrl = ChampionImageBaseUri + champName + ".png";

                try
                {
                    await DownloadFileTaskAsync(new Uri(championUrl), filePath);
                    championImage = Image.FromFile(filePath);
                }
                catch (Exception)
                {
                    championImage = Image.FromFile(ChampNotFoundImageLocation);
                }
            }

            if (size == Size.Empty)
            {
                return championImage;
            }
            else
            {
                return DrawingControl.ResizeImage(championImage, size);
            }
        }

        private async Task<int> GetChampionID(string champName)
        {
            string url = ChampionDataBaseUri + champName + ".json";

            HttpClient client = new();
            try
            {
                HttpResponseMessage response = await client.GetAsync(url).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                string responseBody = await response.Content.ReadAsStringAsync();

                JToken result = JsonConvert.DeserializeObject<JToken>(responseBody);
                JValue key = (JValue)result.Last.First.First.First["key"];

                return Convert.ToInt32(key.Value);
            }
            catch
            {
                return -1;
            }
        }

        private async Task DownloadFileTaskAsync(Uri uri, string FileName)
        {
            HttpClient client = new();

            using Stream s = await client.GetStreamAsync(uri);
            using FileStream fs = new FileStream(FileName, FileMode.CreateNew);
            await s.CopyToAsync(fs);
        }

        private string GetAppSetting(string key)
        {
            var appSettings = ConfigurationManager.AppSettings;
            string result = appSettings[key] ?? "NotFound";

            return result;
        }

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = false)]
        static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr w, IntPtr l);
        private void SetProgressBarState(ProgressBar pBar, ProgressBarState state)
        {
            SendMessage(pBar.Handle, 1040, (IntPtr)state, IntPtr.Zero);
        }

        #endregion Helpers
    }

    public class ProgressBarInfo
    {
        public int Maximum { get; set; }
        public int Value { get; set; }
        public ProgressBarState State { get; set; }
        public Image? ChampionImage { get; set; }
        public Participant? Participant { get; set; }
    }

    public enum ProgressBarState
    {
        Green = 1,
        Red = 2,
        Yellow = 3
    }
}