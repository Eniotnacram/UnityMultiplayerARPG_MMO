﻿using Cysharp.Threading.Tasks;
using LiteNetLibManager;
using UnityEngine;

namespace MultiplayerARPG.MMO
{
    public partial class MMOServerMailHandlers : MonoBehaviour, IServerMailHandlers
    {
#if (UNITY_EDITOR || !EXCLUDE_SERVER_CODES) && UNITY_STANDALONE
        public IDatabaseClient DatabaseClient
        {
            get { return MMOServerInstance.Singleton.DatabaseClient; }
        }
#endif

        public async UniTask<bool> SendMail(Mail mail)
        {
#if (UNITY_EDITOR || !EXCLUDE_SERVER_CODES) && UNITY_STANDALONE
            DatabaseApiResult<SendMailResp> resp = await DatabaseClient.SendMailAsync(new SendMailReq()
            {
                Mail = mail,
            });
            if (resp.IsSuccess && !resp.Response.Error.IsError())
                return true;
#endif
            return false;
        }
    }
}
