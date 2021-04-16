/*************************************************************************************************
 * 
 * FixApiTest
 * 
 * cTrader FIX APIお試し用cBot。
 * 例外処理など省略してるため本格的に利用する際はご注意ください。
 * 
 * Copyright(c) 2021 ajinori
 * https://www.merryoneslife.com/
 * Released under the MIT license.
 * https://opensource.org/licenses/MIT
 * 
 * ***********************************************************************************************/

using System;
using System.Text;
using System.Threading;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Linq;
using System.Collections.Generic;
using cAlgo.API;
using System.Net.Security;
using System.Net.Sockets;

//========================================================================
// cBot本体　主にUI用。
//========================================================================
namespace cAlgo.Robots {
    [Robot(TimeZone = TimeZones.UTC, AccessRights = AccessRights.FullAccess)]
    public class FixApiTest : Robot {

        [Parameter(DefaultValue = "", Group="FIX Connection Info")]
        public string HostName { get; set; }
        [Parameter("a/c", DefaultValue = "", Group = "FIX Connection Info")]
        public string AccountNumber { get; set; }
        [Parameter(DefaultValue = "", Group = "FIX Connection Info")]
        public string SenderCompID { get; set; }
        [Parameter(DefaultValue = "", Group = "FIX Connection Info")]
        public string Password { get; set; }
        [Parameter("SymbolName", DefaultValue = "EURUSD", Group = "Settings")]
        public string MySymbolName { get; set; }

        //---表示するSymbol
        long _fixSymbolID;
        double _volume;
        string _priceFormat;

        //---FIX 接続用
        FixTrade _trader;
        FixQuote _priceProvider;

        //---表示用コントロール
        TextBlock _bid;
        TextBlock _ask;

        //---Heartbeat用
        int _count = 0;

        //--------------
        // OnStart
        protected override void OnStart() {
            // --- a/cパラメータはいらないかも
            if (AccountNumber == "") {
                var index = SenderCompID.LastIndexOf(".")+1;
                if (index == -1) Stop();
                AccountNumber = SenderCompID.Substring(index,SenderCompID.Length-index); ;
            }

            // --- Symbolの情報をセット
            var symbol = Symbols.GetSymbolInfo(MySymbolName);
            if(symbol==null) {
                Print("SymbolNameパラメータを確認してください。");
                Stop();
            }
            _fixSymbolID = symbol.Id;
            _volume = symbol.VolumeInUnitsMin;
            _priceFormat = "f" + symbol.Digits.ToString();

            // --- 発注用のTRADEと価格取得用のQUOTE両方ともログオンしておく
            _trader = new FixTrade(HostName, AccountNumber, Password, SenderCompID);
            _priceProvider = new FixQuote(HostName, AccountNumber, Password, SenderCompID);
            var istlogon = _trader.LogOn();
            PrintMessage("trade logon", _trader);
            var isplogon = _priceProvider.LogOn();
            PrintMessage("quote logon", _priceProvider);
            if (!istlogon || !isplogon) {
                Print("LogOnに失敗しました");
                Stop();
            }

            // --- UI作成
            InitializeControls();

            // --- Timerスタート
            Timer.Start(new TimeSpan(0, 0, 0,0, 200));
        }

        //--------------
        // UI作成
        private void InitializeControls() {
            var panel = new WrapPanel {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = 10,
            };
            var symbol = new TextBlock {
                Text = MySymbolName,
                FontSize = 14,
                Margin = 5,
                VerticalAlignment = VerticalAlignment.Center,
            };
            _bid = new TextBlock {
                Margin = 5 ,
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 14,
            };
            _ask = new TextBlock {
                Margin = 5 ,
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 14,
            };
            var sell = new Button {
                Text = "Sell",
                Margin = 5,
                FontSize = 16,
                BackgroundColor = Chart.ColorSettings.SellColor,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var buy = new Button {
                Text = "Buy",
                Margin = 5,
                FontSize = 16,
                BackgroundColor = Chart.ColorSettings.BuyColor,
                VerticalAlignment = VerticalAlignment.Center,
            };
            panel.AddChild(symbol);
            panel.AddChild(_bid);
            panel.AddChild(_ask);
            panel.AddChild(sell);
            panel.AddChild(buy);
            sell.Click += _ => {
                // --- tradetype=2が売り、２つめの引数はFixSymbolIDおそらくEURUSDならどこも１。
                _trader.ExecuteMarketOrderWithFix(2, _fixSymbolID, _volume);
                PrintMessage("sell order", _trader);
            };
            buy.Click += _ => {
                // --- tradetype=1が買い
                _trader.ExecuteMarketOrderWithFix(1, _fixSymbolID, _volume);
                PrintMessage("buy order", _trader);
            };
            Chart.AddControl(panel);

        }

        //-------------------------------
        // 200ms毎に実行する価格更新処理
        protected override void OnTimer() {
            var prices = _priceProvider.GetPrices(_fixSymbolID);
            if (prices != null) {
                _bid.Text = prices[0].ToString(_priceFormat);
                _ask.Text = prices[1].ToString(_priceFormat);
            }

            // --- 20秒おきくらいにハートビート送っておく
            _count++;
            if (_count > 100) {
                _trader.HeartBeat();
                PrintMessage("heart beat", _trader, false);
                _priceProvider.HeartBeat();
                PrintMessage("heart beat", _priceProvider, false);
                _count = 0;
            }
        }

        //-----------
        // OnStop
        protected override void OnStop() {
            Timer.Stop();
            _trader.LogOut();
            PrintMessage("logout", _trader);
            _priceProvider.LogOut();
            PrintMessage("logout", _priceProvider);
        }

        //----------------------
        // メッセージをログ出力
        private void PrintMessage(string action, FixApiBase fix, bool hasResponse=true) {
            Print(action);
            Print("send : [{0}]", fix.SendMessages.Last().Replace("\u0001", " | "));
            if(hasResponse) Print("receive : [{0}]", fix.ReceiveMessages.Last().Replace("\u0001", " | "));
        }
    }

    //===============================================
    // FIXAPIでメッセージをやり取りするためのクラス
    //===============================================
    public enum ConnectionType { Quote, Trade };
    public class FixApiBase {
        public List<string> SendMessages { get; private set; }
        public List<string> ReceiveMessages { get; private set; }

        protected int _seqNumber=1;

        private readonly string _senderSubID;
        private readonly string _targetCompID = "CSERVER"; //これは固定
        private string _userName;
        private string _password;
        private string _senderCompID;
        private bool _isLogin=false;

        private TcpClient _client;
        private SslStream _sslStream;

        //---------------------------
        // コンストラクタ - SSL通信準備
        public FixApiBase(string hostName, string userName, string password, string senderCompId, ConnectionType type) {
            int port;
            if (type == ConnectionType.Quote) {
                port = 5211;
                _senderSubID = "QUOTE";
            } else if (type == ConnectionType.Trade) {
                port = 5212;
                _senderSubID = "TRADE";
            } else {
                throw new ArgumentException();
            }
            _userName = userName;
            _password = password;
            _senderCompID = senderCompId;
            _client = new TcpClient(hostName, port);
            _sslStream = new SslStream(_client.GetStream(), false, new RemoteCertificateValidationCallback((_, __, ___, error) => error == SslPolicyErrors.None), null);
            _sslStream.AuthenticateAsClient(hostName);
            _sslStream.ReadTimeout = 10000;

            SendMessages = new List<string>();
            ReceiveMessages = new List<string>();
        }

        //-----------------------------
        // ログオン
        public bool LogOn() {
            var body = new StringBuilder();
            // --- 暗号化(98)なし、ハートビート間隔(108)30秒
            //     cTrader FIX APIは暗号化非対応
            body.Append("98=0|108=30|");
            // --- ユーザー名(553)、パスワード(554)
            body.Append("553=" + _userName + "|");
            body.Append("554=" + _password + "|");

            // --- メッセージタイプ＝ログオン(A)のヘッダを付ける
            var msg = AppendHeader(FixMessageType.LogOn, body.ToString());

            // --- 送れる形にして送る
            msg = NormalizeMessage(msg);
            var res = SendMessage(msg);
            Debug.Print("logon response:[{0}]",res);

            // --- ログオンメッセージ("35=A")が返ってきてればログオン成功
            //     失敗してたらログアウトメッセージが返ってくる
            if (res.Contains("35=A")) {
                _isLogin = true;
                return true;
            } else {
                Debug.Print("logon error (response:[{0}])\r\n", res);
                return false;
            }
        }
        //-------------------------------
        // ログアウト
        public void LogOut() {
            if (_isLogin) {
                var msg = AppendHeader(FixMessageType.LogOut, String.Empty);
                msg = NormalizeMessage(msg);
                var res = SendMessage(msg);
                Debug.Print("logout response:[{0}]", res);
            }
        }
        //-------------------------------
        // ハートビート-セッション維持用
        public void HeartBeat() {
            var msg = AppendHeader(FixMessageType.Heartbeat, String.Empty);
            msg = NormalizeMessage(msg);
            SendMessage(msg, false);
        }
        //----------------------------
        // ヘッダー追加
        protected string AppendHeader(string msgType, string body){

            var headerTail = new StringBuilder();
            // --- メッセージタイプ(35)、送信元送信先ID(49,56,57,50)、メッセージ連番(34)、送信日時(52)
            headerTail.Append("35=" + msgType + "|");
            headerTail.Append("49=" + _senderCompID + "|");
            headerTail.Append("56=" + _targetCompID + "|");
            headerTail.Append("57=" + _senderSubID + "|");
            headerTail.Append("50=" + _senderSubID + "|");
            headerTail.Append("34=" + _seqNumber + "|");
            headerTail.Append("52=" + DateTime.UtcNow.ToString("yyyyMMdd-HH:mm:ss") + "|");


            var header = new StringBuilder();
            // --- FIXバージョン(8)4.4固定、後ろのメッセージの長さ(9)
            header.Append("8=FIX.4.4|");
            var length = headerTail.Length + body.Length;
            header.Append("9=" + length + "|");

            // --- heder前後くっつけて、本体とつなげて返す
            header.Append(headerTail);
            return header.Append(body).ToString();
        }

        //---------------------------------------------------
        // SOH置換してチェックサム追加
        // 計算方法はSpotWareのFixAPI Sampleからそのまま拝借
        protected string NormalizeMessage(string headAndBody) {
            var message = headAndBody.Replace("|", "\u0001");
            byte[] byteSeq = Encoding.ASCII.GetBytes(message);
            int sum = 0;
            foreach (byte ch in byteSeq) {
                sum += ch;
            }
            var checksum = sum % 256;
            var traler = "10=" + checksum.ToString().PadLeft(3, '0') + "\u0001";
            return message + traler;
        }

        //--------------------------------
        // メッセージ送って返信もらう
        protected string SendMessage(string message, bool hasResponse = true) {
            Debug.Print("send message:[{0}]", message);
            SendMessages.Add(message);
            var byteArray = Encoding.ASCII.GetBytes(message);
            _sslStream.Write(byteArray, 0, byteArray.Length);
            var buffer = new byte[1024];
            _seqNumber++;
            if (hasResponse) {
                Thread.Sleep(100);
                _sslStream.Read(buffer, 0, 1024);
            }
            var response = Encoding.ASCII.GetString(buffer).TrimEnd('\0');
            ReceiveMessages.Add(response);
            return response;

        }

        //-----------------------------
        // 返信メッセージのタグを読む
        static protected string GetTagValue(string message, string tag, int index = 0) {
            var pattern = "\u0001" + tag + "=.+?\u0001";
            var match = Regex.Match(message, pattern);
            for (int i = 0; i < index; i++) match = match.NextMatch();
            if (match.Success) {
                var str = match.Value;
                var res = str.Substring(tag.Length + 2, str.Length - tag.Length - 3);
                return res;
            } else return "";
        }
    }
    //==========================
    // QUOTE用サブクラス
    //==========================
    public class FixQuote : FixApiBase{

        public FixQuote(string hostName, string userName, string password, string senderCompId) :
            base(hostName, userName, password, senderCompId, ConnectionType.Quote) {
        }
        //-----------------------
        // BidとAskの取得開始
        public double[] GetPrices(long fixSymbolID) {
            var body = new StringBuilder();
            // --- データ取得用ユニークID(262)とりあえずSymbolIDでいいや
            body.Append("262=" + fixSymbolID.ToString() + "|");
            // --- サブスクリプションタイプ(263) 1でアップデート、２なら前の破棄。１にしておく。
            body.Append("263=1|");
            // --- 板情報(264) 0はFull、１ならSpot、スポットでいいので１
            body.Append("264=1|");
            // --- ここは固定 更新タイプ(265)、取得する価格の数(267)２つ、受け取る価格(269)BidとAsk(Offer)
            body.Append("265=1|267=2|269=0|269=1|");
            // --- シンボル数(146)はとりあえず１つだけ。シンボルID(55)は引数から
            body.Append("146=1|");
            body.Append("55=" + fixSymbolID + "|");
            var msg = AppendHeader(FixMessageType.MarketDataRequest, body.ToString());
            msg = NormalizeMessage(msg);
            var res = SendMessage(msg);
            Debug.Print("getPrices responce:[{0}]", res);


            // --- ちゃんと値が返ってきたら（＝MessageTypeがXかWだったら）値を返す
            if (res.Contains("35=X") || res.Contains("35=W")) {
                // --- 返信の価格タグ(270)を取得
                var bid = GetTagValue(res, "270");
                var ask = GetTagValue(res, "270", 1);
                Debug.Print("bid={0}, ask={1}", bid, ask);
                return new double[2] { double.Parse(bid), double.Parse(ask) };
            } else {
                return null;
            }
        }

    }
    //==========================
    // TRADE用サブクラス
    //==========================
    public class FixTrade : FixApiBase {
        public FixTrade(string hostName, string userName, string password, string senderCompId) :
            base(hostName, userName, password, senderCompId, ConnectionType.Trade) {
        }
        //-----------------
        // 注文
        public void ExecuteMarketOrderWithFix(int tradeType, long fixSymbolID, double volume) {
            var body = new StringBuilder();
            var time = DateTime.UtcNow.ToString("yyyyMMdd-HH:mm:ss");

            // --- オーダーID(11):本当はクライアント側で管理すべき。とりあえず連番の数字入れておく
            body.Append("11=" + _seqNumber + "|");
            // --- シンボルID(55),売買方向(54) 1で買、２で売、注文時間(60)、発注量(38)
            body.Append("55=" + fixSymbolID+ "|");
            body.Append("54=" + tradeType + "|");
            body.Append("60=" + time + "|");
            body.Append("38=" + volume + "|");
            // --- 注文タイプ(40)　１はマーケット(成行)注文、２は指値逆指値注文
            body.Append("40=1|");
            // --- 有効期限(59), 1はGTC、３はIOC（FOKは４だがcTraderは非対応）
            //     Market注文なので３でいいはず・・・（なぜかSpotWareのサンプルは逆？？）
            body.Append("59=3|");

            // --- ヘッダーつけて送れる形にして送る
            var msg = AppendHeader(FixMessageType.NewOrderSigle, body.ToString());
            msg = NormalizeMessage(msg);
            var res = SendMessage(msg);
            Debug.Print("market order response:" + res);
        }

    }
    //=========================
    // メッセージタイプ
    //=========================
    public static class FixMessageType {
        public const string Heartbeat = "0";
        public const string TestRequest = "1";
        public const string LogOn = "A";
        public const string LogOut = "5";
        public const string ResendRequest = "2";
        public const string SequenceReset = "4";
        public const string MarketDataRequest = "V";
        public const string MarketDataSnapshot = "W";
        public const string MarketDataIncrementalRefresh = "X";
        public const string NewOrderSigle = "D";
        public const string OrderStatusRequest = "H";
        public const string OrderMassStatusRequest = "AF";
        public const string BisinessMessageReject = "j";
        public const string RequestForPositions = "AN";
        public const string PositionReport = "AP";
        public const string OrderCancelRequest = "F";
        public const string OrderCancelReport = "9";
        public const string OrderCancelReplaceRequest = "G";
        public const string MarketDataREquestReject = "Y";
        public const string SecurityListRequest = "x";
        public const string SecurityList = "y";
    }
}