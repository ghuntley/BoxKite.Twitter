﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BoxKite.Twitter;
using BoxKite.Twitter.Console.Helpers;

namespace BoxKite.LiveFireTests
{
    public class DirectMessagesLiveFireTests
    {
        public async Task<bool> DoDMTest(IUserSession session, List<int> testSeq)
        {
            var successStatus = true;
            try
            {
                // 1
                long dmid = 0;
                if (testSeq.Contains(1))
                {
                    ConsoleOutput.PrintMessage("3.1 DirectMessages\\GetDirectMessages", ConsoleColor.Gray);
                    var directmessages1 = await session.GetDirectMessages();

                    if (!directmessages1.ToList()[0].twitterFaulted)
                    {
                        dmid = directmessages1.ToList()[0].Id;
                        foreach (var x in directmessages1)
                        {
                            ConsoleOutput.PrintMessage(
                                String.Format("From: {0} // Message: {1}", x.SenderScreenName, x.Text ));
                        }
                    }
                    else
                        successStatus = false;
                }

                // 2
                if (testSeq.Contains(2))
                {
                    ConsoleOutput.PrintMessage("3.2 DirectMessages\\GetDirectMessageSingle", ConsoleColor.Gray);
                    var directmessages2 = await session.GetDirectMessageSingle(dmid);

                    if (!directmessages2.twitterFaulted)
                    {
                            ConsoleOutput.PrintMessage(
                                String.Format("From: {0} // Message: {1}", directmessages2.SenderScreenName, directmessages2.Text));
                    }
                    else
                        successStatus = false;
                }

                // 3
                if (testSeq.Contains(3))
                {
                    ConsoleOutput.PrintMessage("3.3 DirectMessages\\SendDirectMessage", ConsoleColor.Gray);
                    var directmessages3 =
                        await session.SendDirectMessage("livefire test of boxkite.twitter please ignore", "neilfinn");

                    if (!directmessages3.twitterFaulted)
                    {
                        ConsoleOutput.PrintMessage(
                            String.Format("From: {0} // Message: {1}", directmessages3.SenderScreenName, directmessages3.Text));
                    }
                    else
                        successStatus = false;
                }
            }
            catch (Exception e)
            {
                ConsoleOutput.PrintError(e.ToString());
                return false;
            }
            return successStatus;
        }
    }
}