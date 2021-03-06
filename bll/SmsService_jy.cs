﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using model;
using System.Data;
using cmppV2;
using System.Threading;
using System.Reflection;
using System.Data.SqlClient;

namespace bll
{
    public class SmsService_jy
    {

        private SendThread _cmppSend;

        private Dictionary<int, SendThread> _cstDic = null;//cmpp企业用户
        private Dictionary<int, AccountInfoModel> _cmppAccountDic = null;//cmpp账号
        private Thread _collectThread;
        private ManualResetEvent _manualre = new ManualResetEvent(false);
        private TimeoutCache cache = null;//缓存
        private Timer _collDataTimer;//取数据的线程
        private SMDQueue[] ReportQueue;//report状态队列
        private SMDQueue[] ReportSeqQueue;//状态流水队列
        private SMDQueue[] SendQueue;//提交结果队列
        private SMDQueue[] Submit_RestQueue;//提交响应队列
        private int _submitQueueNum = 10;
        private int _reportQueueNum = 10;
        //private LocalParams lp = null;
        private string[] Keyword = { "注册", "密码", "验证" };
        ActiveMQClient _activeMQ = null;
        bool _isReadFromActive = false;
        public bool Stop()
        {
            try
            {
                if (GlobalModel.ServiceIsStop)
                {
                    return true;
                }
                SMSLog.Debug("SmsService==>Stop=>开始停止>>>>>");
                GlobalModel.IsStopCollect = true;
                try
                {
                    if (_activeMQ != null)
                    {
                        _activeMQ.Stop();
                    }
                    SMSLog.Debug("SmsService==>Stop=>_activeMQ停止>>>>>");
                }
                catch (Exception e1)
                {

                    SMSLog.Debug("SmsService==>Stop ActiveMQ 异常：" + e1.Message);
                }
                try
                {
                    if (_collectThread != null)
                    {
                        _collectThread.Abort();
                        _collectThread.Join();
                    }
                    SMSLog.Debug("SmsService==>Stop=>_collectThread停止>>>>>");
                }
                catch (Exception)
                {

                }

                try
                {

                    if (_collDataTimer != null)
                    {
                        _collDataTimer.Dispose();
                    }
                    SMSLog.Debug("SmsService==>Stop=>_collDataTimer停止>>>>>");
                }
                catch (Exception)
                {

                }

                try
                {
                    if (_cmppSend != null)
                    {
                        // MyTools.WaitTime(1000*10);
                        GlobalModel.WaitSendQueue.WaitOne();//等待发送队列发完
                        _cmppSend.Exit();
                    }

                    SMSLog.Debug("SmsService==>Stop=>_cmppSend停止>>>>>");
                }
                catch (Exception e2)
                {
                    SMSLog.Debug("SmsService==>Stop _cmppSend 异常：" + e2.Message);
                }

                try
                {
                    if (SendQueue != null && SendQueue.Length > 0)
                    {
                        foreach (SMDQueue smsq in SendQueue)
                        {
                            smsq.Stop();
                        }
                    }
                }
                catch (Exception e3)
                {
                    SMSLog.Debug("SmsService==>Stop SubmitQueue 异常：" + e3.Message);
                }



                try
                {
                    if (Submit_RestQueue != null && Submit_RestQueue.Length > 0)
                    {
                        foreach (SMDQueue smsq in Submit_RestQueue)
                        {
                            smsq.Stop();
                        }
                    }
                }
                catch (Exception e3)
                {
                    SMSLog.Debug("SmsService==>Stop Submit_RestQueue 异常：" + e3.Message);
                }

                try
                {
                    if (ReportQueue != null && ReportQueue.Length > 0)
                    {
                        foreach (SMDQueue smsq in ReportQueue)
                        {
                            smsq.Stop();
                        }
                    }
                }
                catch (Exception e3)
                {
                    SMSLog.Debug("SmsService==>Stop ReportQueue 异常：" + e3.Message);
                }

                try
                {
                    if (ReportSeqQueue != null && ReportSeqQueue.Length > 0)
                    {
                        foreach (SMDQueue smsq in ReportSeqQueue)
                        {
                            smsq.Stop();
                        }
                    }
                }
                catch (Exception)
                {

                }

                try
                {
                    if (threadStart != null)
                    {
                        threadStart.Abort();
                        threadStart.Join();
                    }
                }
                catch (Exception)
                {

                }
                GC.Collect();
                SMSLog.Debug("SmsService==>Stop=>服务已停止");
                GlobalModel.ServiceIsStop = true;
            }
            catch (Exception ex)
            {
                SMSLog.Error("SmsService_jy::Stop:Exception:", ex.Message);

            }
            GlobalModel.SetStatusStripInfoHandler(false);
            return false;
        }
        Thread threadStart;
        public bool Start()
        {
            threadStart = new Thread(new ThreadStart(StartServer));
            threadStart.Start();
            return true;
        }
        IDBExec dbexec = null;
        private void StartServer()
        {
            try
            {
                //this.lp = new LocalParams();
                dbexec = (IDBExec)Assembly.Load("bll").CreateInstance(GlobalModel.Lparams.SqlExecClassName);//获取实例

                if (dbexec == null)
                {
                    SMSLog.Debug("SmsService==>Init=>sqltype失败[" + GlobalModel.Lparams.SqlExecClassName + "]");
                    return;
                }
                if (!dbexec.IsConn(GlobalModel.Lparams.SqlConnStr))
                {
                    SMSLog.Debug("SmsService==>Init=>sql登录失败[" + GlobalModel.Lparams.SqlConnStr + "]");
                    return;
                }
                IDBExec.ConnectionstringLocalTransaction = GlobalModel.Lparams.SqlConnStr;

                if (string.IsNullOrEmpty(GlobalModel.Lparams.ActiveMQ_Url))
                {
                    GlobalModel.IsUseActiveMq = false;
                }
                else
                {
                    GlobalModel.IsUseActiveMq = true;
                    _activeMQ = new ActiveMQClient();
                    int dataid = 1;
                    int.TryParse(GlobalModel.Lparams.DataId, out dataid);
                    _activeMQ.DataId = dataid;
                    _isReadFromActive = _activeMQ.InitCustomer(GlobalModel.Lparams.ActiveMQ_Url, GlobalModel.Lparams.ActiveMQ_Name, dbexec);
                }

                if (!GlobalModel.IsUseActiveMq)
                {
                    SMSLog.Debug("SmsService_jy==>Init初始状态.....");
                    //初始数据标识
                    string sql1 = "update EMAS_BASE..TBL_SMS_SNDTMP set SMS_FLAG=0 Where  SMS_OPERATOR=" + GlobalModel.Lparams.GateWayNum + " AND (SMS_FLAG=1 or SMS_FLAG=2)";

                    int re1 = MsSqlDBExec.ExecuteNonQuery(sql1);
                    SMSLog.Debug("SmsService_jy==>Init初始内容表ok：" + re1);
                }


                _cstDic = new Dictionary<int, SendThread>();
                _cmppAccountDic = new Dictionary<int, AccountInfoModel>();

                AccountInfoModel m = new AccountInfoModel();
                m.eprId = 1;
                m.loginname = GlobalModel.Lparams.LoginName;
                m.password = GlobalModel.Lparams.Password;
                m.senddelay = GlobalModel.Lparams.Senddelay;
                m.serviceid = GlobalModel.Lparams.ServiceId;
                m.serviceIp = GlobalModel.Lparams.ServiceIp;
                m.servicePort = int.Parse(GlobalModel.Lparams.ServicePort);
                m.spid = GlobalModel.Lparams.Spid;
                m.spnumber = GlobalModel.Lparams.Spnumber;
                _cmppSend = new SendThread(m);
                if (_cmppSend.Login())
                {

                    _cstDic.Add(m.eprId, _cmppSend);
                    _cmppAccountDic.Add(m.eprId, m);
                    _cmppSend.Start();
                }
                else
                {
                    GlobalModel.IsStopCollect = true;
                    GlobalModel.ServiceIsStop = true;
                    SMSLog.Debug("SmsService_JY==>Init[" + m.loginname + "]登录失败");
                    return;
                }



                int cachetime = GlobalModel.Lparams.CacheTime * 60;
                cache = new TimeoutCache(cachetime);//缓存
                ReportQueue = new SMDQueue[_reportQueueNum];
                ReportSeqQueue = new SMDQueue[_reportQueueNum];
                SendQueue = new SMDQueue[_submitQueueNum];
                Submit_RestQueue = new SMDQueue[_submitQueueNum];
                int timesubmit = 1000;
                int timesreport = 1000;


                for (int i = 0; i < _submitQueueNum; i++)
                {
                    string title = "Submit_" + i;
                    SendQueue[i] = new SMDQueue(title);
                    SendQueue[i].Start(5);
                }

                for (int i = 0; i < _submitQueueNum; i++)
                {
                    string title = "Submit_Rest_" + i;
                    Submit_RestQueue[i] = new SMDQueue(title);
                    Submit_RestQueue[i].Start(timesubmit);
                }

                for (int i = 0; i < _reportQueueNum; i++)
                {
                    string title = "Report_" + i;
                    ReportQueue[i] = new SMDQueue(title, 10, 10);
                    ReportQueue[i].Start(timesreport);

                }

                for (int i = 0; i < _reportQueueNum; i++)
                {

                    string seqt = "ReportSeq_" + i;
                    ReportSeqQueue[i] = new SMDQueue(seqt, 1, 10);
                    ReportSeqQueue[i].Start(timesreport);

                }

                GlobalModel.TransferDataProcHandler = this.TransferDataProc;
                GlobalModel.TransferDataHandler = this.TransferData;
                GlobalModel.UpdateReportStateHandler = this.UpdateReportState;
                GlobalModel.UpdateSubmitStateHandler = this.UpdateSubmitState;
                GlobalModel.SaveMoHandler = this.SaveMo;

                // CollectObject_JY co = new CollectObject_JY();
                //_collDataTimer = new Timer(new TimerCallback(CollectThread), co, GlobalModel.Lparams.ReadContentDealy, GlobalModel.Lparams.ReadContentDealy);

                _collectThread = new Thread(new ThreadStart(CollectThread));
                _collectThread.Start();

                SMSLog.Debug("SmsService_JY==>[Init]启动成功");

                GlobalModel.IsStopCollect = false;
                GlobalModel.ServiceIsStop = false;
                GlobalModel.SetStatusStripInfoHandler(true);
            }
            catch (Exception ex)
            {

                SMSLog.Error("SmsService_jy::StartServer:Exception:", ex.ToString());
            }

        }




        public void AddQueue(SmsModel sms)
        {
            try
            {
                bool ispriority = false;
                foreach (string str in Keyword)
                {
                    if (sms.content.Contains(str))
                    {
                        ispriority = true;
                        break;
                    }
                }
                QueueItem qi = new QueueItem();
                qi.InQueueTime = DateTime.Now;
                qi.MsgObj = sms;
                qi.MsgState = 0;
                qi.MsgType = (uint)Cmpp_Command.CMPP_SUBMIT;
                qi.Sequence = sms.id;
                if (ispriority)
                {
                    _cmppSend.AddPriorityQueue(qi);
                }
                else
                {
                    _cmppSend.AddNormalQueue(qi);
                }
            }
            catch (Exception ex)
            {

                SMSLog.Error("SmsService_jy::AddQueue:Exception:", ex.Message);
            }


        }

        private void CollectThread()
        {
            while (!_manualre.WaitOne(GlobalModel.Lparams.ReadContentDealy, true))
            {

                try
                {
                    DateTime dt1 = DateTime.Now;
                    DataTable dt = null;
                    if (GlobalModel.IsStopCollect)
                    {
                        SMSLog.Debug("SmsService_jy::CollectThread:服务已停不在收集");
                        continue;
                    }

                    //if (_cmppSend.GetQueuqNum() > 1000)
                    //{
                    //    SMSLog.Error("SmsService_jy:CollectThread：短信收集忙碌");
                    //    continue;
                    //}


                    if (GlobalModel.IsUseActiveMq)
                    {
                        try
                        {
                            dt = _activeMQ.GetMessageFromActiveMQ(GlobalModel.Lparams.ReadMobileNum);
                        }
                        catch (Exception ex)
                        {
                            try
                            {
                                _activeMQ.Stop();
                            }
                            catch { }

                            SMSLog.Error("=>SMDDatabase::CollectThread", "取出ActionMQ短信异常: " + ex.Message);
                            ActiveMQClient activeMQ = new ActiveMQClient();
                            _isReadFromActive = activeMQ.InitCustomer(GlobalModel.Lparams.ActiveMQ_Url, GlobalModel.Lparams.ActiveMQ_Name, dbexec);
                            if (_isReadFromActive)
                            {
                                _activeMQ = activeMQ;
                            }
                            else
                            {
                                SMSLog.Error("=>SMDDatabase::CollectThread", "重新初始ActionMQ异常: " + activeMQ.ExceptionMsg);
                            }

                            //dt = this.LoadMsgFromActiveDB();//从新机制数据库中取数据
                            //SMSLog.Error("=>SMDDatabase::CollectThread", "LoadTmpMsgActiveDB 从数据库读取");
                        }
                    }
                    else
                    {

                        dt = GetLoadMsgSql();//
                    }
                    if (dt != null && dt.Rows.Count > 0)
                    {
                        if (dt != null && dt.Rows.Count > 0)
                        {
                            for (int i = 0; i < dt.Rows.Count; i++)
                            {
                                SmsModel msg = new SmsModel();
                                if (FillMessage(dt.Rows[i], msg))
                                {
                                    AddQueue(msg);//加入待发队列
                                    AddCache(msg.id + "", msg);//加入缓存
                                }
                            }
                        }
                    }
                    int con = dt == null ? 0 : dt.Rows.Count;
                    if (con > 0)
                    {
                        SMSLog.Debug("=>SmsService::CollectThread", "取出[" + con + "]条信息打包发送用时[" + (DateTime.Now - dt1).TotalMilliseconds + "]毫秒");

                    }
                }
                catch (Exception ex)
                {

                    SMSLog.Error("SmsService_jy::CollectThread:Exception:", ex.Message);
                }

            }
        }



        private void CollectThread(object obj)
        {
            DateTime dt1 = DateTime.Now;
            DataTable dt = null;
            try
            {

                if (GlobalModel.IsStopCollect)
                {
                    SMSLog.Debug("SmsService_jy::CollectThread:服务已停不在收集");
                    return;
                }

                //if (_cmppSend.GetQueuqNum() > 1000)
                //{
                //    SMSLog.Error("SmsService_jy:CollectThread：短信收集忙碌");
                //    continue;
                //}


                if (GlobalModel.IsUseActiveMq)
                {
                    try
                    {
                        dt = _activeMQ.GetMessageFromActiveMQ(GlobalModel.Lparams.ReadMobileNum);
                    }
                    catch (Exception ex)
                    {
                        try
                        {
                            _activeMQ.Stop();
                        }
                        catch { }

                        SMSLog.Error("=>SMDDatabase::CollectThread", "取出ActionMQ短信异常: " + ex.Message);
                        ActiveMQClient activeMQ = new ActiveMQClient();
                        _isReadFromActive = activeMQ.InitCustomer(GlobalModel.Lparams.ActiveMQ_Url, GlobalModel.Lparams.ActiveMQ_Name, dbexec);
                        if (_isReadFromActive)
                        {
                            _activeMQ = activeMQ;
                        }
                        else
                        {
                            SMSLog.Error("=>SMDDatabase::CollectThread", "重新初始ActionMQ异常: " + activeMQ.ExceptionMsg);
                        }

                        //dt = this.LoadMsgFromActiveDB();//从新机制数据库中取数据
                        //SMSLog.Error("=>SMDDatabase::CollectThread", "LoadTmpMsgActiveDB 从数据库读取");
                    }
                }
                else
                {

                    dt = GetLoadMsgSql();//
                }
                if (dt != null && dt.Rows.Count > 0)
                {
                    if (dt != null && dt.Rows.Count > 0)
                    {
                        for (int i = 0; i < dt.Rows.Count; i++)
                        {
                            SmsModel msg = new SmsModel();
                            if (FillMessage(dt.Rows[i], msg))
                            {
                                AddQueue(msg);//加入待发队列
                                AddCache(msg.id + "", msg);//加入缓存
                            }
                        }
                    }
                }
                int con = dt == null ? 0 : dt.Rows.Count;
                if (con > 0)
                {
                    SMSLog.Debug("=>SmsService::CollectThread", "取出[" + con + "]条信息打包发送用时[" + (DateTime.Now - dt1).TotalMilliseconds + "]毫秒");
                }

            }
            catch (Exception ex)
            {

                SMSLog.Error("SmsService_jy::CollectThread:Exception:", ex.Message);
            }
        }



        private void AddCache(string msgid, SmsModel msg)
        {
            try
            {
                cache.Add(msgid, msg);
            }
            catch (Exception ex)
            {
                SMSLog.Error("SmsService_jy::[" + msgid + "],Id=" + msg.id + " AddCache:Exception:", ex.Message);
            }
        }

        private void DelCache(string msgid)
        {
            try
            {
                cache.Del(msgid);
            }
            catch (Exception ex)
            {
                SMSLog.Error("SmsService_jy::[" + msgid + "],Id=" + msgid + " DelFromCache:Exception:", ex.Message);
            }
        }

        private SmsModel GetFormCache(string msgid)
        {
            try
            {
                SmsModel obj = cache.Get(msgid) as SmsModel;
                return obj;
            }
            catch (Exception ex)
            {
                SMSLog.Error("SmsService:: GetFormCache:Exception:", ex.Message);
            }
            return null;
        }


        private bool FillMessage(DataRow row, SmsModel msg)
        {
            try
            {
                if (GlobalModel.IsUseActiveMq)
                {

                    try
                    {
                        msg.id = UInt32.Parse(row["ID"].ToString());
                        msg.sendId = int.Parse(row["SNDID"].ToString());
                        msg.userId = row["UCUID"].ToString();
                        msg.mobile = row["MOBILE"].ToString();
                        msg.eprId = int.Parse(row["EPRID"].ToString());
                        if (!row.IsNull("EXTNUM"))
                        {
                            msg.subId = row["EXTNUM"].ToString();
                        }
                        else
                        {
                            msg.subId = "";
                        }
                        if (!row.IsNull("CLIENTMSGID"))
                        {
                            msg.clientMsgId = row["CLIENTMSGID"].ToString();
                        }
                        else
                        {
                            msg.clientMsgId = "";
                        }
                        msg.cid = row["CID"].ToString();
                        if (!row.IsNull("CONTENT"))
                        {
                            msg.content = row["CONTENT"].ToString();
                        }
                        else
                        {
                            msg.content = GetContent(msg.cid);
                        }


                    }
                    catch (Exception ex)
                    {
                        SMSLog.Error("SMDDatabase::FillMessage", "解析数据异常: " + ex.Message);
                    }
                }
                else
                {
                    msg.id = UInt32.Parse(row["SND_ID"].ToString());//消息ID
                    msg.content = row["SMS_CONTENT"].ToString();
                    msg.mobile = row["USER_MBLPHONE"].ToString();
                    msg.subId = row["SMS_SUBID"].ToString();
                    msg.clientMsgId = row["SMS_CLIENTID"].ToString();
                    msg.operatorId = int.Parse(row["SMS_OPERATOR"].ToString());
                    msg.userId = row["UCUID"].ToString();
                }
                //SMSLog.Debug("SmsService==>FillMessage[SrcId]：" + GlobalModel.Lparams.SrcId);
                msg.srcnum = GlobalModel.Lparams.SrcId + msg.subId;
                if (msg.srcnum.Length > 21)
                {
                    msg.srcnum = msg.srcnum.Substring(0, 21);
                }
                //SMSLog.Debug("SmsService==>FillMessage[msg.srcnum]：" + msg.srcnum);


                /*
                 string srcid = "";
                if (GlobalModel.Lparams.SrcId.Equals("10690468"))
                {
                    if (msg.userId.Equals("2840") || msg.userId.Equals("2845") || msg.userId.Equals("2846") || msg.userId.Equals("2841"))
                    {
                        srcid = GlobalModel.Lparams.SrcId + "10";
                    }
                    else if (msg.userId.Equals("2842"))//交警局扩展号122
                    {
                        srcid = GlobalModel.Lparams.SrcId;
                    }
                    else if (msg.userId.Equals("2843"))//绿联办扩展号1221
                    {
                        srcid = GlobalModel.Lparams.SrcId;
                    }
                    else if (msg.userId.Equals("2849"))//潮州
                    {
                        srcid = GlobalModel.Lparams.SrcId + "13";
                    }
                    else
                    {
                        srcid = GlobalModel.Lparams.SrcId + "88";
                    }
                    srcid += msg.subId.Trim();

                }
                else
                {
                    srcid = GlobalModel.Lparams.SrcId + msg.subId;
                }
                if (string.IsNullOrEmpty(srcid))
                {
                    msg.srcnum = srcid;
                }
                else
                {
                    msg.srcnum = "";
                }
                 * */
                return true;
            }
            catch (Exception ex)
            {
                SMSLog.Debug("SmsService==>FillMessage[Exception]：" + ex.Message);
            }
            return false;
        }


        private string GetContent(string cid)
        {
            string content = RedisManager.GetVal<string>("sms" + cid);
            if (string.IsNullOrEmpty(content))
            {
                string sql = string.Format("select content from TBL_SMS_CONTENT where ID={0}", cid);
                object obj = this.dbexec.ExecuteScalar(sql);
                content = obj == null ? "" : obj.ToString();
            }
            return content;
        }




        public DataTable LoadMsgFromActiveDB()
        {

            try
            {

                string gateway = GlobalModel.Lparams.GateWayNum + "";
                string sql = "select top " + GlobalModel.Lparams.ReadMobileNum + " ID,ID AS SNDID,MOBILE,CID,UCUID,EXTNUM,CLIENTMSGID,EPRID  from  TBL_SMS_MOBILES where SENDTIME< dateadd(mi,5,getdate()) and  [GATEWAY]=" + gateway + " and [STATUS] = 1";
                DataTable dt = MsSqlDBExec.GetDateTable(CommandType.Text, sql, null);
                if (dt != null && dt.Rows.Count > 0)
                {

                    dt.Columns.Add("CONTENT");

                    StringBuilder sb = new StringBuilder();
                    for (int i = 0; i < dt.Rows.Count; i++)
                    {
                        string cid = dt.Rows[i]["CID"].ToString();
                        string txt = this.GetContent(cid);
                        dt.Rows[i]["CONTENT"] = txt;
                        sb.Append(dt.Rows[i]["ID"]);
                        sb.Append(",");
                    }

                    string updatesql = string.Format("update TBL_SMS_MOBILES set [STATUS]=5 where ID IN({0})", sb.ToString().Trim(','));
                    int re = MsSqlDBExec.ExecuteNonQuery(CommandType.Text, updatesql, null);
                    if (re != dt.Rows.Count)
                    {
                        return null;
                    }
                }
                return dt;
            }
            catch (Exception ex)
            {

                SMSLog.Error("SMDDatabase=>LoadMsgFromActiveDB:Exception:" + ex.ToString());
            }
            return null;
        }

        object getlock = new object();
        protected DataTable GetLoadMsgSql()
        {
            DataTable dt = null;
            lock (getlock)
            {
                try
                {

                    Random rd = new Random();
                    int kdsid = rd.Next();

                    string selectsql = string.Format("select top {0} *  from EMAS_BASE..TBL_SMS_SNDTMP with(nolock) where  SMS_OPERATOR ={1} and SMS_INTIME< dateadd(mi,1,getdate()) and  SMS_TIMER < 3 and SMS_FLAG = 0   order by SMS_PRIORITY desc,SMS_INTIME", GlobalModel.Lparams.ReadMobileNum, GlobalModel.Lparams.GateWayNum);
                    dt = MsSqlDBExec.GetDateTable(CommandType.Text, selectsql, null);
                    if (dt != null && dt.Rows.Count > 0)
                    {
                        StringBuilder sb = new StringBuilder();
                        for (int i = 0; i < dt.Rows.Count; i++)
                        {
                            sb.Append(dt.Rows[i]["SND_ID"]);
                            sb.Append(",");
                        }
                        string upsql = string.Format("update EMAS_BASE..TBL_SMS_SNDTMP set KDS_IID={0},SMS_FLAG = 2,SMS_TIMER=SMS_TIMER+1 where SMS_OPERATOR ={1} and SND_ID in ({2})", kdsid, GlobalModel.Lparams.GateWayNum, sb.ToString().Trim(','));
                        if (dt.Rows.Count == 1)
                        {
                            upsql = string.Format("update EMAS_BASE..TBL_SMS_SNDTMP set KDS_IID={0},SMS_FLAG = 2,SMS_TIMER=SMS_TIMER+1 where SMS_OPERATOR ={1} and SND_ID ={2}", kdsid, GlobalModel.Lparams.GateWayNum, sb.ToString().Trim(','));
                        }
                        int ure = MsSqlDBExec.ExecuteNonQuery(CommandType.Text, upsql, null);
                        if (ure < 1)
                        {
                            return null;
                        }
                    }



                }
                catch (Exception ex)
                {
                    dt = null;
                    SMSLog.Error("SMDDatabase=>GetLoadMsgSql:Exception:" + ex.ToString());
                }
                finally
                {
                    //GlobalModel.CollectDataAutoResetEvent.Set();
                }
            }
            return dt;
        }



        /// <summary>
        /// 加载待发短信
        /// </summary>
        /// <returns></returns>
        protected DataTable LoadMessageProc()
        {

            try
            {

                int kds = MyTools.GetRand();

                SqlParameter[] cmdparam =  
               { 
                new SqlParameter("@maxnum",SqlDbType.Int),//top
                new SqlParameter("@operator",SqlDbType.Int),//网关编号
                new SqlParameter("@kiid",SqlDbType.BigInt)
               };
                // SMSLog.Error("SmsService_jy=>LoadTmpMsg:ReadMobileNum:" + lp.ReadMobileNum);
                cmdparam[0].Value = GlobalModel.Lparams.ReadMobileNum;
                cmdparam[1].Value = GlobalModel.Lparams.GateWayNum;
                cmdparam[2].Value = kds;
                SqlStatementModel sm = new SqlStatementModel();
                sm.CmdTxt = "proc_loadtmpmsg";
                sm.MsSqlCmdParms = cmdparam;
                sm.CmdType = CommandType.StoredProcedure;
                //SMSLog.Debug(sm.ToString());
                return MsSqlDBExec.GetDateTable(CommandType.StoredProcedure, "proc_loadtmpmsg", cmdparam);
            }
            catch (Exception ex)
            {

                SMSLog.Error("SmsService_jy=>LoadTmpMsg:Exception:" + ex.ToString());
            }
            return null;
        }

        /// <summary>
        /// 转移数据
        /// </summary>
        /// <param name="id"></param>
        private void TransferData(int id, int operatorId)
        {

            try
            {
                int operId = GlobalModel.Lparams.GateWayNum;
                string sql = string.Format("Delete From EMAS_BASE..TBL_SMS_SNDTMP OUTPUT DELETED.SND_ID,DELETED.SERVICEFEE_ID,DELETED.SERVICE_ID,DELETED.USER_MBLPHONE,DELETED.SMS_MSGFORMAT,DELETED.SMS_SPPORT,DELETED.SMS_CONTENT,DELETED.SMS_SERVICEID,DELETED.SMS_FEECODE,DELETED.FEE_CURRENTBAS,DELETED.SMS_FEETYPE,DELETED.SMS_FEEMSISDN,DELETED.SMS_REPORT,DELETED.SMS_INTIME,getdate(),DELETED.SMS_TIMER,DELETED.SMS_OPERATOR/10,{0},DELETED.SMS_REPORTSTATUS,DELETED.SMS_LINKID,'',DELETED.SMS_MOMTFLAG,DELETED.SMS_PRIORITY,DELETED.SMS_FLAG,DELETED.SMS_OVERWRITE,DELETED.SMS_FEEKIND,DELETED.SMS_SNDKIND,DELETED.KDS_IID,DELETED.SMS_CLIENTID,DELETED.CP_ID,DELETED.SMS_BATID,DELETED.SMS_SUBID,DELETED.UCUID,DELETED.SMS_COUNT  INTO EMAS_BASE..TBL_SMS_SNDCACHE(SND_ID,SERVICEFEE_ID,SERVICE_ID,USER_MBLPHONE,SMS_MSGFORMAT,SMS_SPPORT,SMS_CONTENT,SMS_SERVICEID,SMS_FEECODE,FEE_CURRENTBAS,SMS_FEETYPE,SMS_FEEMSISDN,SMS_REPORT,SMS_INTIME,SMS_SNDTIME,SMS_TIMER,SMS_OPERATOR,SMS_STATUS,SMS_REPORTSTATUS,SMS_LINKID,SMS_MSGID,SMS_MOMTFLAG,SMS_PRIORITY,SMS_FLAG,SMS_OVERWRITE,SMS_FEEKIND,SMS_SNDKIND,KDS_IID,SMS_CLIENTID,CP_ID,SMS_BATID,SMS_SUBID,UCUID,SMS_COUNT ) WHERE SMS_OPERATOR={1} and SND_ID ={2};", 0, operId, id);

                int re = 0;
                int i = 0;
                do
                {
                    re = MsSqlDBExec.ExecuteNonQuery(CommandType.Text, sql, null);
                    i++;
                } while (re <= 0 && i < 3);
                if (re == 0)
                {
                    SqlStatementModel sm = new SqlStatementModel();
                    sm.CmdTxt = sql;
                    sm.CmdType = CommandType.Text;
                    sm.MsSqlCmdParms = null;
                    this.AddReprotQueue(sm);
                    SMSLog.Debug("SmsService==>UpdateMobileSubmitState[FailSql]：" + sql);
                }
            }
            catch (Exception ex)
            {
                SMSLog.Error("SmsService_jy=>TransferData:Exception:" + ex.ToString());

            }

        }


        private void TransferDataProc(int id, int operatorId, int submitstatus, string msgid)
        {
            
            if (GlobalModel.IsUseActiveMq)
            {
                TransferDataProc_ActiveMQ(id, operatorId, submitstatus, msgid);
                return;
            }


            SmsModel msg = this.GetFormCache(id + "");
            try
            {
                SqlStatementModel sm = new SqlStatementModel();
                SqlParameter[] cmdparam =
                    {                 
                    new SqlParameter("@operator",SqlDbType.Int),//网关编号               
                    new SqlParameter("@snd_id",SqlDbType.Int),//标识列
                    new SqlParameter("@state",SqlDbType.Int),//提交状态
                    new SqlParameter("@msgid",SqlDbType.VarChar)//msgid
                    };
                cmdparam[0].Value = operatorId;
                cmdparam[1].Value = id;
                cmdparam[2].Value = submitstatus;
                cmdparam[3].Value = msgid;
                sm.MsSqlCmdParms = cmdparam;
                sm.CmdType = CommandType.StoredProcedure;
                int i = 0;
                int re = 0;
                do
                {
                    i++;
                    re = MsSqlDBExec.ExecuteNonQuery(CommandType.StoredProcedure, "proc_TransferTmptoCache", cmdparam);

                    if (re == 0)
                    {
                        DateTime now = DateTime.Now;
                        while (now.AddMilliseconds(10) > DateTime.Now)
                        {
                        }
                    }
                } while (re == 0 && i < 3);
                if (re == 0)
                {
                    sm.CmdTxt = "proc_TransferTmptoCache";
                    this.AddReprotQueue(sm);
                    // SMSLog.Debug("SmsService_jy:: SubmitStatus:FailProc", "proc_TransferTmptoCache " + operatorId + "," + id + "," + submitstatus + "," + msgid);
                }
                else
                {
                    SMSLog.Debug("SmsService_jy=>TransferDataProc:[" + re + "][" + i + "]", "proc_TransferTmptoCache " + operatorId + "," + id + "," + submitstatus + "," + msgid);

                }


                if (submitstatus != 0 && msg != null)//提交失败的，写入状态流水表
                {
                    //添加到状态流水状态表
                    object[] val = { msg.mobile, msgid, operatorId, 1, "Notify -1", msg.clientMsgId, msg.userId };
                    string stateseq = string.Format("Insert Into EMAS_BASE..TBL_SMS_REPORTSEQ(USER_MBLPHONE, SMS_MSGID, SMS_OPERATOR, SMS_STATUS, SMS_REPORTSTATUS, SMS_SNDTIME, SMS_DELIVERDTIME, SMS_CLIENTID, CP_ID) values ('{0}', '{1}', '{2}', '{3}', '{4}', getdate(),getdate(), '{5}', '{6}')", val);
                    SqlStatementModel smseq = new SqlStatementModel();
                    smseq.CmdTxt = stateseq;
                    smseq.CmdType = CommandType.Text;
                    smseq.MsSqlCmdParms = null;
                    this.AddReprotQueue(smseq);

                }
            }
            catch (Exception ex)
            {

                SMSLog.Error("SmsService_jy=>TransferDataProc:Exception:" + ex.ToString());
            }
        }


        private void TransferDataProc_ActiveMQ(int id, int operatorId, int submitstatus, string msgid)
        {
            try
            {
                SmsModel msg = this.GetFormCache(id + "");
                int state = 0;
                if (submitstatus == 0)
                {
                    state = 3;
                }
                else
                {
                    state = 4;
                }
                int gateway = GlobalModel.Lparams.GateWayNum;
                string srcno = msg == null ? "" : msg.srcnum;
                string sql = string.Format("update TBL_SMS_MOBILES set status={0},statuscode='{1}',[GATEWAY]={2} where CID={3} and MOBILE='{4}'", state, submitstatus, gateway, msg.cid, msg.mobile);
                if (msg != null && msg.sendId > 0)
                {
                    sql = string.Format("update TBL_SMS_MOBILES set status={0},statuscode='{1}',[GATEWAY]={2} where ID={3}", state, submitstatus, gateway, msg.sendId);
                }

                int re = MsSqlDBExec.ExecuteNonQuery(CommandType.Text, sql, null);
                SMSLog.Debug("SubmitResult[" + re + "]:", sql);//只更新一次

                /*
                if (submitstatus == 0)
                {
                    int re = MsSqlDBExec.ExecuteNonQuery(CommandType.Text, sql, null);
                    SMSLog.Debug("SubmitResult[" + re + "]:", sql);//只更新一次
                }
                else
                {
                    SqlStatementModel sm = new SqlStatementModel();
                    sm.CmdTxt = sql;
                    sm.CmdType = CommandType.Text;
                    sm.MsSqlCmdParms = null;
                    this.AddSendQueue(sm);
                }
                */

                //if (re == 0)
                //{
                //    SqlStatementModel sm = new SqlStatementModel();
                //    sm.CmdTxt = sql;
                //    sm.CmdType = CommandType.Text;
                //    sm.MsSqlCmdParms = null;
                //    this.AddSendQueue(sm);
                //}

                if (submitstatus != 0 && msg != null && !string.IsNullOrEmpty(msg.clientMsgId))//提交失败的，写入状态流水表
                {
                    //添加到状态流水状态表
                    object[] val = { msg.mobile, msgid, operatorId, 1, "Notify -1", msg.clientMsgId, msg.userId, msg.eprId };
                    string stateseq = string.Format("Insert Into JY15.EMAS_BASE.dbo.TBL_SMS_REPORTSEQ(USER_MBLPHONE, SMS_MSGID, SMS_OPERATOR, SMS_STATUS, SMS_REPORTSTATUS, SMS_SNDTIME, SMS_DELIVERDTIME, SMS_CLIENTID, CP_ID,EPRID) values ('{0}', '{1}', '{2}', '{3}', '{4}', getdate(),getdate(), '{5}', '{6}',{7})", val);
                    SqlStatementModel smseq = new SqlStatementModel();
                    smseq.CmdTxt = stateseq;
                    smseq.CmdType = CommandType.Text;
                    smseq.MsSqlCmdParms = null;
                    SMSLog.Debug(" TransferDataProc_ActiveMQ:ReportSql", stateseq);

                    
                    try
                    {
                        HttpHelper hh = new HttpHelper();
                        string sndid = msg.sendId == 0 ? "" : msg.sendId + "";
                        object[] obj = { sndid, msg.mobile, submitstatus, hh.UrlEncoderUTF8(DateTime.Now.ToString()), msg.clientMsgId, msg.userId, msg.eprId, msg.cid };
                        string requesturl = string.Format("http://192.168.10.174/smsStatusManage/receiveSmsStatusServlet?cmd=smsGatewaySubmitFail&smsMobliesId={0}&userMblphone={1}&smsStatus=1&statusCode={2}&smsDeliverdTime={3}&smsClientId={4}&cpid={5}&eprId={6}&cid={7}", obj);
                        string getre = hh.Get(requesturl, "UTF-8").Trim();
                        SMSLog.Debug("提交失败推送", "getre=" + getre + ",url=" + requesturl);
                        if (!getre.Contains("成功"))
                        {
                            this.AddReprot_SeqQueue(smseq);
                        }
 
                        
                    }
                    catch (Exception ex)
                    {
                        SMSLog.Error("UpdateSubmitStatus提交失败推送", ex.Message);
                    }




                }

            }
            catch (Exception ex)
            {

                SMSLog.Error("SmsService_jy=>TransferDataProc:IsUseActiveMq==>Exception:" + ex.ToString());
            }
        }

        /// <summary>
        /// 提交结果，转移数据，更新msgid 
        /// </summary>
        /// <param name="args">[id,msgid,state]</param>
        private void UpdateSubmitState(string[] args)
        {
            try
            {
                string id; string msgid; string state;
                id = args[0];
                msgid = args[1];
                state = args[2];
                int submitState = 1;
                SmsModel sms = this.GetFormCache(id);
                bool isreturn = false;
                try
                {

                    if (sms != null && sms.GetReportMsgIdCount > 0 && state.Equals("0"))
                    {
                        SMSLog.Debug("SmsService==>UpdateSubmitState[不更新]：" + "msgidcount=" + sms.GetReportMsgIdCount + ",msgid=" + msgid + ",id=" + id);
                        isreturn = true;
                    }
                    else
                    {
                        if (sms != null)
                        {
                            sms.SaveReportMsgId = msgid;
                        }
                    }
                    if (state.Equals("0"))
                    {
                        submitState = 0;
                    }
                    else
                    {
                        submitState = 1;
                    }

                    if (sms != null)
                    {
                        if (sms.ReportMsgIdLst != null)
                        {
                            ReportMsgidStatus rms = new ReportMsgidStatus();
                            rms.Msgid = msgid;
                            rms.SubmitState = state;
                            sms.ReportMsgIdLst.Add(rms);
                        }
                        else
                        {
                            List<ReportMsgidStatus> lst = new List<ReportMsgidStatus>();
                            ReportMsgidStatus rms = new ReportMsgidStatus();
                            rms.Msgid = msgid;
                            rms.SubmitState = state;
                            lst.Add(rms);
                            sms.ReportMsgIdLst = lst;
                        }

                        this.AddCache(msgid, sms);
                        this.DelCache(id);
                        this.AddCache(id, sms);
                    }
                }
                catch (Exception ex)
                {
                    SMSLog.Error("SmsService_jy=>UpdateSubmitState:MSGID==>Exception:" + ex.ToString());
                }

                if (isreturn)
                {
                    return;
                }

                int operId = GlobalModel.Lparams.GateWayNum;

                if (GlobalModel.IsUseActiveMq)
                {
                    UpdateSubmitState_ActiveMQ(args);
                    return;
                }
                else
                {
                    string sql = string.Format("Update EMAS_BASE..TBL_SMS_SNDCACHE set SMS_STATUS={0},SMS_MSGID='{1}',SMS_SNDTIME=getdate(),SMS_STATUSCODE='{2}' where SND_ID={3} ", submitState, msgid, state, id);

                    SMSLog.Debug("SmsService==>NotifySendedStatus[FailSql]：" + sql);
                    int re = 0;
                    int i = 0;
                    do
                    {
                        re = MsSqlDBExec.ExecuteNonQuery(CommandType.Text, sql, null);
                        i++;
                        if (re == 0)
                        {
                            DateTime now = DateTime.Now;
                            while (now.AddMilliseconds(10) > DateTime.Now)
                            {
                            }
                        }

                    } while (re == 0 && i < 3);

                    if (re == 0)
                    {
                        SqlStatementModel sm = new SqlStatementModel();
                        sm.CmdTxt = sql;
                        sm.CmdType = CommandType.Text;
                        sm.MsSqlCmdParms = null;
                        this.AddReprotQueue(sm);
                        SMSLog.Debug("SmsService==>UpdateMobileSubmitState[FailSql]：" + sql);
                    }



                    //失败状态，写入状态流水表
                    if (!state.Equals("0") && sms != null)
                    {
                        //添加到状态流水状态表
                        object[] val = { sms.mobile, msgid, operId, 1, "Notify -1", sms.clientMsgId, sms.userId, state };
                        string stateseq = string.Format("Insert Into EMAS_BASE..TBL_SMS_REPORTSEQ(USER_MBLPHONE, SMS_MSGID, SMS_OPERATOR, SMS_STATUS, SMS_REPORTSTATUS, SMS_SNDTIME, SMS_DELIVERDTIME, SMS_CLIENTID, CP_ID,STATUSCODE) values ('{0}', '{1}', '{2}', '{3}', '{4}', getdate(),getdate(), '{5}', '{6}','{7}')", val);
                        SqlStatementModel smseq = new SqlStatementModel();
                        smseq.CmdTxt = stateseq;
                        smseq.CmdType = CommandType.Text;
                        smseq.MsSqlCmdParms = null;
                        this.AddReprotQueue(smseq);
                    }
                }

            }
            catch (Exception ex)
            {

                SMSLog.Error("SmsService_jy=>UpdateSubmitState:Exception:" + ex.ToString());
            }


        }

        private void UpdateSubmitState_ActiveMQ(string[] args)
        {

            try
            {
                string id; string msgid; string state;
                id = args[0];
                msgid = args[1];
                state = args[2];


                SmsModel sms = this.GetFormCache(id);
                SqlStatementModel sm = new SqlStatementModel();
                int newstate = 0;
                if (state.Equals("0"))
                {
                    newstate = 3;
                }
                else
                {
                    newstate = 4;
                }
                string srcno = sms == null ? "" : sms.srcnum;
                int gateway = GlobalModel.Lparams.GateWayNum;
                string sql = "";
                if (sms != null)
                {
                    sql = string.Format("update TBL_SMS_MOBILES set status={0},msgid='{1}',statuscode='{2}',DISPLAYNUM='{3}',[GATEWAY]={4} where CID={5} and MOBILE='{6}'", newstate, msgid, state, srcno, gateway, sms.cid, sms.mobile);
                    if (sms.sendId > 0)
                    {
                        sql = string.Format("update TBL_SMS_MOBILES set status={0},msgid='{1}',statuscode='{2}',DISPLAYNUM='{3}',[GATEWAY]={4} where ID={5}", newstate, msgid.Trim(), state, srcno, gateway, sms.sendId);
                    }
                }
                else
                {
                    sql = string.Format("update TBL_SMS_MOBILES set status={0},msgid='{1}',statuscode='{2}',DISPLAYNUM='{3}',[GATEWAY]={4} where ID={5}", newstate, msgid.Trim(), state, srcno, gateway, id);
                }
                 
                if (state.Equals("0"))
                {
                    int re = MsSqlDBExec.ExecuteNonQuery(CommandType.Text, sql, null);
                    SMSLog.Debug("SubmitResp[" + re + "]", sql);
                    if (re == 0)
                    {
                        //SqlStatementModel sm = new SqlStatementModel();
                        sm.CmdTxt = sql;
                        sm.CmdType = CommandType.Text;
                        sm.MsSqlCmdParms = null; 
                        this.AddSubmit_RespQueue(sm);
                    }
                    /*
                    int re = MsSqlDBExec.ExecuteNonQuery(CommandType.Text, sql, null);
                    SMSLog.Debug("UpdateSubmitState_ActiveMQ=>NotifySendedStatus[" + re + "]", sql); 
                    if (re == 0)
                    {
                       SqlStatementModel sm = new SqlStatementModel();
                       sm.CmdTxt = sql;
                       sm.CmdType = CommandType.Text;
                       sm.MsSqlCmdParms = null;
                       SMSLog.Debug("SMDDatabase:: UpdateSubmitState_ActiveMQ:FailSql", sql);
                       this.AddReprotQueue(sm);
                    }
                    */
                }else//提交失败的，写入状态流水表
                {
                    //添加到状态流水状态表
                    object[] val = { sms.mobile, msgid, GlobalModel.Lparams.GateWayNum, 1, "Notify -1", sms.clientMsgId, sms.userId, sms.eprId };
                    string stateseq = string.Format("Insert Into JY15.EMAS_BASE.dbo.TBL_SMS_REPORTSEQ(USER_MBLPHONE, SMS_MSGID, SMS_OPERATOR, SMS_STATUS, SMS_REPORTSTATUS, SMS_SNDTIME, SMS_DELIVERDTIME, SMS_CLIENTID, CP_ID,EPRID) values ('{0}', '{1}', '{2}', '{3}', '{4}', getdate(),getdate(), '{5}', '{6}',{7})", val);
                    SqlStatementModel smseq = new SqlStatementModel();
                    smseq.CmdTxt = sql;
                    smseq.CmdType = CommandType.Text;
                    smseq.MsSqlCmdParms = null;
                    SMSLog.Debug("ReportSql", sql);
                    //this.AddReprotQueue(smseq);

                     
                    try
                    {
                        HttpHelper hh = new HttpHelper();
                        string sndid = sms.sendId == 0 ? "" : sms.sendId + "";
                        object[] obj = { sndid, sms.mobile, state, hh.UrlEncoderUTF8(DateTime.Now.ToString()), sms.clientMsgId, sms.userId, sms.eprId, sms.cid };
                        string requesturl = string.Format("http://192.168.10.174/smsStatusManage/receiveSmsStatusServlet?cmd=smsGatewaySubmitFail&smsMobliesId={0}&userMblphone={1}&smsStatus=1&statusCode={2}&smsDeliverdTime={3}&smsClientId={4}&cpid={5}&eprId={6}&cid={7}", obj);
                        string getre = hh.Get(requesturl, "UTF-8").Trim();
                        SMSLog.Debug("UpdateSubmitState_ActiveMQ", "getre=" + getre + ",url=" + requesturl);
                        if (!getre.Contains("成功"))
                        {
                            this.AddSubmit_RespQueue(sm);
                            this.AddReprotQueue(smseq);
                        }
  
                        
                    }
                    catch (Exception ex)
                    {
                        SMSLog.Error("UpdateSubmitState_ActiveMQ", ex.Message);
                    }




                }

            }
            catch (Exception ex)
            {

                SMSLog.Error("SmsService_jy=>TransferDataProc:IsUseActiveMq==>Exception:" + ex.ToString());
            }
        }


        private void AddSendQueue(SqlStatementModel sqlsm)
        {
            try
            {
                if (sqlsm != null)
                {
                    Random rand = new Random();
                    int i = rand.Next(0, _submitQueueNum);
                    SendQueue[i].AddSql(sqlsm);
                }
            }
            catch (Exception ex)
            {
                SMSLog.Error("SmsService_jy=>AddSendQueue:Exception:" + ex.ToString());
            }
        }

        private void AddSubmit_RespQueue(SqlStatementModel sqlsm)
        {
            try
            {
                if (sqlsm != null)
                {
                    Random rand = new Random();
                    int i = rand.Next(0, _submitQueueNum);
                    Submit_RestQueue[i].AddSql(sqlsm);
                }
            }
            catch (Exception ex)
            {

                SMSLog.Error("SmsService_jy=>AddSubmit_RespQueue:Exception:" + ex.ToString());
            }
        }


        private void AddReprotQueue(SqlStatementModel sqlsm)
        {
            try
            {
                if (sqlsm != null)
                {
                    Random rand = new Random();
                    int i = rand.Next(0, _reportQueueNum);
                    ReportQueue[i].AddSql(sqlsm);
                }
            }
            catch (Exception ex)
            {

                SMSLog.Error("SmsService_jy=>AddReprotQueue:Exception:" + ex.ToString());
            }
        }


        private void AddReprot_SeqQueue(SqlStatementModel sqlsm)
        {
            try
            {
                if (sqlsm!=null)
                {
                Random rand = new Random();
                int i = rand.Next(0, _reportQueueNum);
                ReportSeqQueue[i].AddSql(sqlsm);
                }
            }
            catch (Exception ex)
            {

                SMSLog.Error("SmsService_jy=>AddReprot_SeqQueue:Exception:" + ex.ToString());
            }
        }

        /// <summary>
        /// 更新状态回执
        /// </summary>
        /// <param name="args">[msgid,srcnumber,reportstatus,mobile]</param>
        private void UpdateReportState(string[] args)
        {
            try
            {
                string msgid = ""; string srcnumber = ""; string reportstatus = ""; string mobile = "";
                int operid = 0;
                msgid = args[0];
                srcnumber = args[1];
                reportstatus = args[2];
                mobile = args[3];
                //SMSLog.Debug("=======UpdateReportState:msgid=" + msgid + ",mobile=" + mobile + ",reportstatus=" + reportstatus);
                operid = GlobalModel.Lparams.GateWayNum;
                string status = reportstatus;
                bool isreturn = false;
                if (reportstatus.Equals("DELIVRD") || reportstatus.Equals("0"))
                {
                    status = "Echo 1";
                }
                else
                {
                    status = "Echo -1";
                }


                SmsModel sm = this.GetFormCache(msgid);
                SmsModel smId = null;
                if (sm != null)
                {
                    //SMSLog.Debug("=======UpdateReportState:msgid=" + msgid + ",mobile=" + mobile + ",reportstatus=" + reportstatus + ",sm.id=" + sm.id);
                    smId = this.GetFormCache(sm.id + "");
                    if (smId != null)
                    {
                        SMSLog.Debug("=======UpdateReportState:msgid=" + msgid + ",mobile=" + mobile + ",reportstatus=" + reportstatus + ",sm.id=" + sm.id + ",SaveReportMsgId=" + smId.SaveReportMsgId + ",SaveReportState=" + smId.SaveReportState);
                        if (!string.IsNullOrEmpty(smId.SaveReportState) && reportstatus.Equals(smId.SaveReportState) && !smId.SaveReportMsgId.Equals(msgid))
                        {
                            isreturn = true;
                        }

                        if (reportstatus.Equals(smId.SaveReportState) && msgid.Equals(smId.SaveReportMsgId))
                        {
                            isreturn = true;
                        }

                    }
                }



                try
                {

                    if (smId != null)
                    {
                        //重新加入缓存
                        smId.SetReportMsgIdState(msgid, reportstatus);
                        this.DelCache(sm.id + "");
                        this.AddCache(sm.id + "", smId);
                    }
                }
                catch (Exception ex)
                {
                    SMSLog.Error("SmsService_jy::UpdateReportState:重新加入缓存Exception:" + ex.Message + ",msgid=" + msgid + ",mobile=" + mobile);
                }

                if (isreturn)
                {
                    SMSLog.Debug("SmsService_jy::UpdateReportState:【无需更新】,msgid=" + msgid + ",mobile=" + mobile);
                    return;
                }

                try
                {
                    if (smId != null && !msgid.Equals(smId.SaveReportMsgId))
                    {
                        if ((string.IsNullOrEmpty(smId.SaveReportState) || !reportstatus.Equals("DELIVRD")))
                        {
                            args[0] = smId.SaveReportMsgId;
                            //重新加入缓存
                            smId.SetReportMsgIdState(smId.SaveReportMsgId, reportstatus);
                            this.DelCache(sm.id + "");
                            this.AddCache(sm.id + "", smId);
                            SMSLog.Debug("UpdateReportState:状态回执失败需要更新,原msgid=" + msgid + ",替换成=" + smId.SaveReportMsgId);
                        }
                    }
                }
                catch (Exception ex)
                {
                    SMSLog.Error("UpdateReportState replace msgid Exception:" + ex.Message);
                }


                if (GlobalModel.IsUseActiveMq)
                {
                    UpdateReportState_ActiveMQ(args);
                    return;
                }


                if (reportstatus.Equals("DELIVRD"))
                {


                    if (sm != null)
                    {

                        if (GlobalModel.IsUseActiveMq)
                        {


                        }
                        else
                        {
                            //string statesql = string.Format("Update EMAS_BASE..TBL_SMS_SNDCACHE set SMS_REPORTSTATUS='{0}',[SMS_SPPORT]='{1}',SMS_STATUSCODE='{2}' where SMS_MSGID='{3}' and SMS_STATUS=0", status, srcnumber, reportstatus, msgid);
                            string statesql = string.Format("Update EMAS_BASE..TBL_SMS_SNDCACHE set SMS_REPORTSTATUS='{0}',[SMS_SPPORT]='{1}',SMS_STATUSCODE='{2}' where SND_ID={3} and SMS_STATUS=0", status, srcnumber, reportstatus, sm.id);
                            SqlStatementModel sqlm = new SqlStatementModel();
                            sqlm.MsSqlCmdParms = null;
                            sqlm.CmdTxt = statesql;
                            sqlm.CmdType = CommandType.Text;
                            sqlm.ExecTimer = 1;
                            AddReprotQueue(sqlm);
                            SMSLog.Debug("SMDDatabase::ReportSql:", statesql);

                            try
                            {
                                //添加到状态流水状态表
                                object[] obj = { mobile, msgid, operid, 0, status, sm.clientMsgId, sm.userId, reportstatus };
                                string stateseq = string.Format("Insert Into EMAS_BASE..TBL_SMS_REPORTSEQ(USER_MBLPHONE, SMS_MSGID, SMS_OPERATOR, SMS_STATUS, SMS_REPORTSTATUS, SMS_SNDTIME, SMS_DELIVERDTIME, SMS_CLIENTID, CP_ID,STATUSCODE) values ('{0}', '{1}', '{2}', '{3}', '{4}', getdate(),getdate(), '{5}', '{6}','{7}')", obj);

                                SMSLog.Debug("SmsService_jy:Reportseq", stateseq);
                                SqlStatementModel sm2 = new SqlStatementModel();
                                sm2.MsSqlCmdParms = null;
                                sm2.CmdTxt = stateseq;
                                sm2.CmdType = CommandType.Text;
                                sm2.ExecTimer = 1;
                                AddReprotQueue(sm2);

                            }
                            catch (Exception ex)
                            {

                                SMSLog.Error("SmsService_jy::NotifyReportStatus_Cache(TBL_SMS_REPORTSEQ)==>Exception:" + ex.Message);
                            }

                        }


                    }
                    else
                    {
                        try
                        {

                            SqlStatementModel smsdo = new SqlStatementModel();
                            //smsdo.CmdTxt = "USP_SMG_UpdateMessageReportStatus_donotAgain_201512";
                            smsdo.CmdTxt = "UpdateReportStatus_donotAgain";
                            smsdo.ExecTimer = 0;
                            smsdo.CmdType = CommandType.StoredProcedure;
                            SqlParameter[] cmdParms =
                         { 
                         new SqlParameter("@operid",SqlDbType.Int),
                         new SqlParameter("@msgid",SqlDbType.VarChar),
                         new SqlParameter("@reportstatus",SqlDbType.VarChar),
                         new SqlParameter("@sender",SqlDbType.VarChar),
                         new SqlParameter("@varmobile",SqlDbType.VarChar),
                         new SqlParameter("@statuscode",SqlDbType.VarChar)
                         };
                            cmdParms[0].Value = operid;
                            cmdParms[1].Value = msgid;
                            cmdParms[2].Value = status;
                            cmdParms[3].Value = srcnumber;
                            cmdParms[4].Value = mobile;
                            cmdParms[5].Value = reportstatus;
                            smsdo.MsSqlCmdParms = cmdParms;
                            AddReprotQueue(smsdo);
                            SMSLog.Debug("SmsService_jy::ReportProc", smsdo.ToString());
                        }
                        catch (Exception ex)
                        {

                        }

                    }
                }
                else
                {
                    int sndid = 0;
                    if (sm != null)
                    {
                        sndid = (int)sm.sendId;

                    }

                    SqlStatementModel smsre = new SqlStatementModel();
                    //smsre.CmdTxt = "USP_SMG_UpdateReportStatus_ReSend201512";
                    smsre.CmdTxt = "UpdateReportStatus_ReSend";
                    smsre.ExecTimer = 1;
                    smsre.CmdType = CommandType.StoredProcedure;
                    SqlParameter[] cmdParms =
                    { 
                    new SqlParameter("@snd_id",SqlDbType.Int),
                    new SqlParameter("@operid",SqlDbType.Int),
                    new SqlParameter("@msgid",SqlDbType.VarChar),
                    new SqlParameter("@imobile",SqlDbType.VarChar),
                    new SqlParameter("@sender",SqlDbType.VarChar),
                    new SqlParameter("@reportstatus",SqlDbType.VarChar),
                    new SqlParameter("@statuscode",SqlDbType.VarChar)
                    };
                    cmdParms[0].Value = sndid;
                    cmdParms[1].Value = operid;
                    cmdParms[2].Value = msgid;
                    cmdParms[3].Value = mobile;
                    cmdParms[4].Value = srcnumber;
                    cmdParms[5].Value = status;
                    cmdParms[6].Value = reportstatus;
                    smsre.MsSqlCmdParms = cmdParms;
                    AddReprotQueue(smsre);
                    SMSLog.Debug("SmsService_jy::ReportProc", smsre.ToString());
                }
            }
            catch (Exception ex)
            {

                SMSLog.Error("SmsService_jy=>UpdateReportState:Exception:" + ex.ToString());
            }

        }

        public void UpdateReportState_ActiveMQ(string[] args)
        {
            string sndid = ""; string cpid = ""; string eprId = ""; string cid = "";

            SqlStatementModel smreportSeq = null;
            SqlStatementModel smreport = null;

            string msgid = ""; string srcnumber = ""; string statuscode = ""; string mobile = "";
            int operid = 0;
            try
            {

                msgid = args[0];
                srcnumber = args[1];
                statuscode = args[2];
                mobile = args[3];
                operid = GlobalModel.Lparams.GateWayNum;
                string status = statuscode;
                SmsModel sm = this.GetFormCache(msgid);

                if (statuscode.Equals("DELIVRD"))
                {
                    status = "Echo 1";
                    if (sm != null)
                    {
                        cpid = sm.userId;
                        eprId = sm.eprId + "";
                        cid = sm.cid;
                        string sql = string.Format("update TBL_SMS_MOBILES set [RESULT]={0},[STATUSCODE]='{1}' where MSGID='{2}' and [STATUS]=3", 1, statuscode, msgid);
                        if (sm.sendId > 0)
                        {
                            sndid = sm.sendId + "";
                            sql = string.Format("update TBL_SMS_MOBILES set [RESULT]={0},[STATUSCODE]='{1}' where ID={2} and [STATUS]=3", 1, statuscode, sm.sendId);
                        }
                        int re = 0;// MsSqlDBExec.ExecuteNonQuery(CommandType.Text, sql, null);
                        SMSLog.Debug("UpdateReportState[" + re + "]", sql);
                        if (re == 0)
                        {
                            smreport = new SqlStatementModel();
                            smreport.MsSqlCmdParms = null;
                            smreport.CmdTxt = sql;
                            smreport.CmdType = CommandType.Text;
                            smreport.ExecTimer = 0;
                            //AddReprotQueue(smreport);
                            //SMSLog.Debug("UpdateReportState_ActiveMQ:ReportSql", sql);
                        }
                        if (!string.IsNullOrEmpty(sm.clientMsgId))
                        {
                            //状态流水
                            object[] obj = { mobile, msgid, operid, 0, status, sm.clientMsgId, sm.userId, statuscode, sm.eprId };
                            string stateseq = string.Format("Insert Into jy15.EMAS_BASE.dbo.TBL_SMS_REPORTSEQ(USER_MBLPHONE, SMS_MSGID, SMS_OPERATOR, SMS_STATUS, SMS_REPORTSTATUS, SMS_SNDTIME, SMS_DELIVERDTIME, SMS_CLIENTID, CP_ID,STATUSCODE,EPRID) values ('{0}', '{1}', '{2}', '{3}', '{4}', getdate(),getdate(), '{5}', '{6}','{7}',{8})", obj);
                            // SMSLog.Debug("UpdateReportState_ActiveMQ:Reportseq", stateseq);
                            smreportSeq = new SqlStatementModel();
                            smreportSeq.MsSqlCmdParms = null;
                            smreportSeq.CmdTxt = stateseq;
                            smreportSeq.CmdType = CommandType.Text;
                            smreportSeq.ExecTimer = 1;
                            //AddReprotQueue(smreportSeq); 
                        }



                    }
                    else
                    {

                        //成功
                        SqlStatementModel smnoa = new SqlStatementModel();
                        smnoa.CmdType = CommandType.StoredProcedure;
                        smnoa.CmdTxt = "UpdateMsgReportStatus";
                        SqlParameter[] cmdParms =
                            { 
                                new SqlParameter("@operid",SqlDbType.Int),
                                new SqlParameter("@msgid",SqlDbType.VarChar),
                                new SqlParameter("@reportstatus",SqlDbType.VarChar),
                                new SqlParameter("@statuscode",SqlDbType.VarChar),
                                new SqlParameter("@varmobile",SqlDbType.VarChar)
                             };
                        cmdParms[0].Value = operid;
                        cmdParms[1].Value = msgid;
                        cmdParms[2].Value = "Echo 1";
                        cmdParms[3].Value = statuscode;
                        cmdParms[4].Value = mobile;
                        smnoa.ExecTimer = 1;
                        smnoa.MsSqlCmdParms = cmdParms;
                        SMSLog.Debug("UpdateReportState_ActiveMQ", smnoa.ToString());
                        smreport = smnoa;
                        // AddReprotQueue(smnoa);
                    }

                }//成功
                else
                {
                    int snd_id = 0;
                    if (sm != null)
                    {
                        snd_id = sm.sendId;
                    }
                    //失败补发
                    SqlStatementModel smresend = new SqlStatementModel();
                    smresend.CmdType = CommandType.StoredProcedure;
                    smresend.CmdTxt = "UpdateMsgReportStatus_ReSend";
                    SqlParameter[] cmdParms =
                           { 
                              new SqlParameter("@snd_id",SqlDbType.Int),
                               new SqlParameter("@operid",SqlDbType.Int),
                               new SqlParameter("@msgid",SqlDbType.VarChar),
                               new SqlParameter("@reportstatus",SqlDbType.VarChar),
                               new SqlParameter("@statuscode",SqlDbType.VarChar)
                            
                              // new SqlParameter("@mobile",SqlDbType.VarChar),
                             
                            };
                    cmdParms[0].Value = snd_id;
                    cmdParms[1].Value = operid;
                    cmdParms[2].Value = msgid.Trim();
                    cmdParms[3].Value = "Echo -1";
                    cmdParms[4].Value = statuscode;
                    smresend.MsSqlCmdParms = cmdParms;
                    smresend.ExecTimer = 1;
                    SMSLog.Debug("UpdateReportState_ActiveMQ", smresend.ToString());
                    AddReprotQueue(smresend);
                }
            }
            catch (Exception ex) 
            {
                
                SMSLog.Error("UpdateReportStatus Exception:" + ex.Message);
            }


            try
            {
                HttpHelper hh = new HttpHelper();
                string statetmp = "1";
                if (statuscode.Equals("DELIVRD"))
                {
                    statetmp = "0";
                }
                DateTime dtnow = DateTime.Now;
                object[] obj = { mobile, hh.UrlEncoderUTF8(msgid), statetmp, statuscode, hh.UrlEncoderUTF8(DateTime.Now.ToString()), operid, cpid, eprId, cid };
                string requesturl = string.Format("http://192.168.10.174/smsStatusManage/receiveSmsStatusServlet?cmd=smsStatusReceipt&smsMobliesId=&userMblphone={0}&smsMsgid={1}&smsStatus={2}&statusCode={3}&smsDeliverdTime={4}&smsOperator={5}&cpid={6}&eprId={7}&cid={8}", obj);
                string getre = hh.Get(requesturl, "UTF-8").Trim();
                SMSLog.Debug("PUSH:UpdateReportStatus", "getre=" + getre + ",耗时：" + (DateTime.Now - dtnow).TotalMilliseconds + ",url=" + requesturl);
                if (!getre.Contains("成功"))
                {
                    this.AddReprot_SeqQueue(smreportSeq);
                    AddReprotQueue(smreport);
                }
            }
            catch (Exception ex)
            {
                SMSLog.Error("PUSH:UpdateReportStatus Exception:" + ex.Message);
            }



        }

        private void SaveMo(string[] args)
        {
            try
            {
                string mobile; string recvnumber; string content;
                mobile = args[0];
                recvnumber = args[1];
                content = args[2];
                string subprot = recvnumber.Replace(GlobalModel.Lparams.Spnumber, "").Trim();
                int operid = GlobalModel.Lparams.GateWayNum;

                bool result = false;
                try
                {
                    HttpHelper hh = new HttpHelper();
                    DateTime dtnow = DateTime.Now;
                    object[] obj = { mobile, recvnumber, hh.UrlEncoderUTF8(content), subprot, operid };
                    string requesturl = string.Format("http://192.168.10.174/smsStatusManage/receiveSmsSelAPI?mobile={0}&recvnum={1}&content={2}&extnum={3}&operator={4}", obj);
                    string getre = hh.Get(requesturl, "UTF-8").Trim();
                    if (getre.Contains("成功"))
                    {
                        result = true;
                    }
                    SMSLog.Debug("SaveMo:PUSH", "getre=" + getre + ",耗时：" + (DateTime.Now - dtnow).TotalMilliseconds + ",url=" + requesturl);
                }
                catch (Exception ex)
                {
                    SMSLog.Error("SaveMo Exception:", ex.Message);
                }


                if (!result)
                {
                    SqlStatementModel sm = new SqlStatementModel();
                    sm.CmdTxt = "USP_SMG_RecvMsgByExtNum";
                    sm.CmdType = CommandType.StoredProcedure;
                    sm.ExecTimer = 1;
                    SqlParameter[] cmdparam =  
                       { 
                        new SqlParameter("@mobile",SqlDbType.VarChar),//手机号码        
                        new SqlParameter("@recvnum",SqlDbType.VarChar),//接收号码
                        new SqlParameter("@content",SqlDbType.VarChar),//内容
                        new SqlParameter("@extnum",SqlDbType.VarChar),//扩展号码
                        new SqlParameter("@operator",SqlDbType.Int)//网关编号 
                      };

                    cmdparam[0].Value = mobile;
                    cmdparam[1].Value = recvnumber;
                    cmdparam[2].Value = content;
                    cmdparam[3].Value = subprot;
                    cmdparam[4].Value = operid;
                    sm.MsSqlCmdParms = cmdparam;
                    AddReprotQueue(sm);
                    SMSLog.Debug("SmsService_jy==>SaveMo:RecvSql", sm.ToString());
                }
                
                
            }
            catch (Exception ex)
            {

                SMSLog.Error("SmsService_jy=>SaveMo:Exception:" + ex.ToString());
            }


        }


        private long GetTimesTamp(DateTime dateTime)
        {
            try
            {
                TimeSpan ts = DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, 0);
                long timestamp = Convert.ToInt64(ts.TotalMilliseconds);
                return timestamp;
            }
            catch (Exception ex)
            {

                SMSLog.Error("SmsService_jy=>GetTimesTamp:Exception:" + ex.ToString());
            }
            return DateTime.Now.Ticks;
        }




    }//end

    class CollectObject_JY
    {
        public int EprId { get; set; }
        public int LoadContentNum { get; set; }
        public int LoadMobileNum { get; set; }
    }
}
