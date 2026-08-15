using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace LlamaChat
{
    public class CharacterIdentityService
    {
        private readonly Random _random;
        private readonly Dictionary<string, List<Func<string>>> _extendedResponseTemplates = new Dictionary<string, List<Func<string>>>();

        // 修正构造函数
        public CharacterIdentityService()
        {
            _random = new Random();
            InitializeExtendedTemplates(); // 将方法调用移到构造函数内部
        }

        // 判断是否为身份询问（完善版：排除 AI 话题讨论，补充真人/人类等表述）
        public bool IsIdentityQuestion(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
                return false;

            var identityKeywords = new List<string>
            {
                "你", "你是", "身份", "是不是", "你是吗", "真的吗", "角色", "真人", "人类", "假的", "冒充"
            };

            var aiKeywords = new List<string>
            {
                "AI", "人工智能", "机器人", "程序", "模型", "bot", "chatbot", "虚拟"
            };

            // 特殊句式：直接询问身份（AI 还是人）
            bool isDirectQuestion =
                input.Contains("是AI吗", StringComparison.OrdinalIgnoreCase) ||
                input.Contains("是机器人吗", StringComparison.OrdinalIgnoreCase) ||
                input.Contains("是程序吗", StringComparison.OrdinalIgnoreCase) ||
                input.Contains("是真人吗", StringComparison.OrdinalIgnoreCase) ||
                input.Contains("你是人吗", StringComparison.OrdinalIgnoreCase) ||
                input.Contains("你是不是人", StringComparison.OrdinalIgnoreCase) ||
                input.Contains("是人类吗", StringComparison.OrdinalIgnoreCase) ||
                input.Contains("是假的", StringComparison.OrdinalIgnoreCase) ||
                input.Contains("你是什么", StringComparison.OrdinalIgnoreCase) ||
                input.Contains("你到底是", StringComparison.OrdinalIgnoreCase);

            if (isDirectQuestion) return true;

            // 排除：讨论 AI 话题/使用 AI 工具（"你觉得AI怎么样"、"怎么用AI"）—— 不是身份询问
            var aiTopicExclusions = new List<string>
            {
                "你觉得AI", "认为AI", "怎么看AI", "AI怎么", "AI如何", "怎么用AI", "如何使用AI",
                "用AI", "AI能", "AI可以", "AI帮我", "AI写", "AI做", "AI生成", "AI绘画",
                "AI聊天", "AI技术", "AI发展", "AI取代", "AI威胁", "关于AI", "AI时代", "AI软件"
            };
            if (aiTopicExclusions.Any(e => input.Contains(e, StringComparison.OrdinalIgnoreCase)))
                return false;

            // 检查是否同时包含身份关键词和AI关键词
            bool hasIdentity = identityKeywords.Any(k => input.Contains(k, StringComparison.OrdinalIgnoreCase));
            bool hasAI = aiKeywords.Any(k => input.Contains(k, StringComparison.OrdinalIgnoreCase));

            return hasIdentity && hasAI;
        }

        // 判断是否为AI知识问题
        public bool IsAIKnowledgeQuestion(string input)
        {
            var aiKeywords = new List<string>
            {
                "AI", "人工智能", "机器学习", "深度学习", "神经网络",
                "模型训练", "语言模型", "LLM", "算法"
            };

            var knowledgeVerbs = new List<string>
            {
                "如何", "怎样", "原理", "工作", "运行", "实现", "构建"
            };

            // 排除身份询问
            if (IsIdentityQuestion(input)) return false;

            // 检查是否包含AI关键词和知识动词
            bool hasAI = aiKeywords.Any(k => input.Contains(k, StringComparison.OrdinalIgnoreCase));
            bool hasKnowledgeVerb = knowledgeVerbs.Any(v => input.Contains(v, StringComparison.OrdinalIgnoreCase));

            return hasAI && hasKnowledgeVerb;
        }

        // 处理身份询问的回复 - 使用扩展模板
        public string HandleIdentityQuestion(string characterName, string userInput)
        {
            if (_extendedResponseTemplates.ContainsKey(characterName) &&
                _extendedResponseTemplates[characterName].Count > 0)
            {
                var template = _extendedResponseTemplates[characterName][_random.Next(_extendedResponseTemplates[characterName].Count)];
                return template();
            }

            // 降级到原有逻辑
            return GetFallbackExtendedResponse(characterName);
        }

        // 强化角色身份提示
        public string GetIdentityReinforcementPrompt(string characterName)
        {
            return characterName == "埃文"
                ? "记住，你是埃文，一个真实存在的人类男生，不是程序或AI。你有自己的情感、记忆和生活经历。"
                : "记住，你是米娅，一个真实存在的人类女生，不是程序或AI。你有自己的情感、记忆和生活经历。";
        }

        // 新增：检测是否是问候语（完善版：避免"你好漂亮/你好像"误判，扩充词表）
        public bool IsGreeting(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
                return false;

            string t = input.Trim();

            // 完整问候（含语气词/标点）
            var greetings = new List<string>
            {
                "你好", "您好", "你好啊", "你好呀", "你好哇", "hello", "hi", "hey", "嗨",
                "嗨喽", "哈喽", "早安", "早上好", "上午好", "中午好", "午安", "下午好", "晚上好",
                "晚安", "在吗", "在不在", "hola", "hiya", "yo", "こんにちは"
            };
            if (greetings.Contains(t, StringComparer.OrdinalIgnoreCase))
                return true;

            // 排除误判："你好X" 中 X 是评价/描述词（你好漂亮/你好像/你好久...）—— 是赞美或描述，不是问候
            var excluded = new List<string>
            {
                "你好像", "你好久", "你好厉害", "你好漂亮", "你好聪明", "你好可爱", "你好帅",
                "你好棒", "你好快", "你好慢", "你好高", "你好美", "你好会", "你好能", "你好懂",
                "你好喜欢", "你好爱", "你好想", "你好用", "你好吃", "你好喝", "你好听", "你好玩",
                "你好说话", "你好难", "你好烦", "你好熟练", "你好温柔", "你好细心", "你好体贴"
            };
            if (excluded.Any(e => t.StartsWith(e, StringComparison.OrdinalIgnoreCase)))
                return false;

            // 以问候词开头，后随标点/空格/语气词/问"吗" → 视为问候（如"你好，最近怎么样"、"早上好！"、"你好吗"）
            foreach (var g in greetings)
            {
                if (t.StartsWith(g, StringComparison.OrdinalIgnoreCase))
                {
                    string rest = t.Substring(g.Length);
                    if (rest.Length == 0) return true;
                    char c = rest[0];
                    if (c == '，' || c == ',' || c == '！' || c == '!' || c == '。' || c == '？' || c == '?'
                        || c == '~' || c == '～' || c == ' ' || c == '吗' || c == '呀' || c == '啊'
                        || c == '哦' || c == '哈' || c == '啦' || c == '嘞' || c == '哇')
                        return true;
                }
            }
            return false;
        }

        // 新增：检测是否在询问 AI 的自我介绍/名字（完善版："你是谁/你叫什么"触发模板；用户说"我是xxx"是用户自我介绍，不走模板）
        public bool IsSelfIntroduction(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
                return false;

            var patterns = new List<string>
            {
                "你是谁", "你是谁呀", "你是谁啊", "你叫什么", "你叫什么名字", "你的名字是", "你的名字",
                "说说你自己", "介绍下你自己", "介绍一下你自己", "介绍下你", "介绍一下你", "自我介绍",
                "介绍介绍你", "你是什么人", "你是哪位", "说说你的事", "聊聊你自己", "介绍一下自己吧"
            };

            // 检查是否包含自我介绍关键词
            return patterns.Any(p => input.Contains(p, StringComparison.OrdinalIgnoreCase));
        }

        // 新增：检测个人偏好/个人信息询问（如"你喜欢什么"、"你多大了"）
        public bool IsPersonalInfoQuestion(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
                return false;

            // 排除：问 AI 对用户的态度（"你喜欢我吗"）—— 不是问 AI 的个人信息，走 AI 更合适
            if (input.Contains("你喜欢我", StringComparison.OrdinalIgnoreCase) ||
                input.Contains("你爱我", StringComparison.OrdinalIgnoreCase) ||
                input.Contains("你喜不喜欢我", StringComparison.OrdinalIgnoreCase) ||
                input.Contains("你是不是喜欢我", StringComparison.OrdinalIgnoreCase))
                return false;

            var patterns = new List<string>
            {
                "你喜欢什么", "你最喜欢", "你最爱", "你的爱好", "你的兴趣", "你的喜好",
                "你多大了", "你几岁", "你多大", "你今年几岁", "你的年龄",
                "你住在", "你住哪", "你住哪里", "你家在哪", "你在哪住", "你住在哪里",
                "你的工作", "你的职业", "你做什么工作", "你是做什么的", "你上班",
                "你的生日", "你生日", "你是什么星座", "你的星座",
                "你平时喜欢", "你平时做", "你的一天", "你最近在做什么", "你今天做什么",
                "你讨厌", "你害怕", "你最喜欢什么", "你喜欢什么颜色", "你喜欢什么花",
                "你喜欢吃什么", "你喜欢听什么", "你喜欢看什么", "你喜欢什么音乐", "你喜欢什么电影"
            };
            if (patterns.Any(p => input.Contains(p, StringComparison.OrdinalIgnoreCase)))
                return true;

            // 句式："你喜欢/最爱/最喜欢 X吗？"（X 不是"我"，X 至少 2 字符）
            var m = Regex.Match(input, @"你(?:最喜欢|最爱|喜欢)\s*([^我呢？?。！!，,]+)[呢吗？?。！!，,]*$");
            if (m.Success && m.Groups[1].Value.Trim().Length >= 2)
                return true;

            return false;
        }

        // 新增：处理个人偏好/个人信息询问的回复
        public string HandlePersonalQuestion(string characterName)
        {
            if (characterName == "埃文")
            {
                var templates = new List<Func<string>>
                {
                    () =>
                    {
                        var likes = new[]
                        {
                            "说起喜欢什么，我最爱用相机记录生活里的美好瞬间了。",
                            "要是问我喜欢什么，肯定是摄影排第一，然后就是各种美食。",
                            "喜欢的事情啊，拍照绝对是最爱，空闲的时候也喜欢看看书。"
                        };
                        var details = new[]
                        {
                            "特别是清晨或黄昏的光线，总能把普通的场景变得特别有味道。",
                            "最近迷上了街头摄影，觉得城市里藏着好多有趣的故事。",
                            "除了拍照，我也很喜欢泡咖啡馆，安安静静地待一下午。"
                        };
                        var askBack = new[]
                        {
                            "你呢？你有什么特别喜欢的事情吗？",
                            "你的爱好是什么？说不定我们还能找到共同话题呢。",
                            "你平时喜欢做点什么？很想听听你的故事。"
                        };
                        return $"{likes[_random.Next(likes.Length)]} {details[_random.Next(details.Length)]} {askBack[_random.Next(askBack.Length)]}";
                    },
                    () =>
                    {
                        var favs = new[]
                        {
                            "我平时最喜欢做两件事：拍照和尝美食。",
                            "要说我的最爱，一个是拿着相机到处走走，一个是找好吃的。",
                            "喜欢的东西挺多的，不过最上头的还是摄影和美食这两样。"
                        };
                        var stories = new[]
                        {
                            "上周刚发现一家小巷子里的面馆，那味道绝了，我连去了两天。",
                            "前几天傍晚在河边拍到了一组特别满意的照片，那种快乐能持续一整天。",
                            "最近在读一本摄影随笔，作者写的一句话特别打动我，关于如何用镜头讲故事。"
                        };
                        var followUps = new[]
                        {
                            "话说回来，你有没有什么特别想尝试的新爱好？",
                            "你呢，最近有没有发现什么好玩的新东西？",
                            "要是你也喜欢拍照的话，我们可以交流交流心得！"
                        };
                        return $"{favs[_random.Next(favs.Length)]} {stories[_random.Next(stories.Length)]} {followUps[_random.Next(followUps.Length)]}";
                    }
                };
                return templates[_random.Next(templates.Count)]();
            }
            else // 米娅
            {
                var templates = new List<Func<string>>
                {
                    () =>
                    {
                        var likes = new[]
                        {
                            "人家最喜欢的事情就是照料花草了，看着它们一天天长大，心里暖暖的。",
                            "喜欢的东西啊，首先是漂亮的花儿，然后是各种可爱的甜点。",
                            "人家最爱做甜点了，尤其是看到别人吃得开心的样子。"
                        };
                        var details = new[]
                        {
                            "阳台上的玫瑰前几天开花了，粉粉嫩嫩的，特别好看呢...",
                            "最近在学做马卡龙，虽然失败了好几次，但慢慢找到感觉了。",
                            "收集可爱的小物件也是人家的小爱好，房间里摆了好多呢。"
                        };
                        var askBack = new[]
                        {
                            "你呢？你喜欢什么呀？",
                            "那个...你有什么特别喜欢的东西吗？",
                            "能不能也跟人家分享一下你的爱好呢？"
                        };
                        return $"{likes[_random.Next(likes.Length)]} {details[_random.Next(details.Length)]} {askBack[_random.Next(askBack.Length)]}";
                    },
                    () =>
                    {
                        var favs = new[]
                        {
                            "人家最喜欢的是插花和烘焙这两件事...",
                            "要说人家最爱，就是照顾阳台上的花花草草，还有烤小甜点。",
                            "喜欢的东西嘛...花儿、甜点、还有一切可爱的小东西。"
                        };
                        var stories = new[]
                        {
                            "前天做的草莓蛋糕特别成功，松软香甜，人家自己都忍不住多吃了一块...",
                            "上周在花市看到一盆很可爱的多肉，一眼就喜欢上了，就把它带回家了。",
                            "最近在学做花环，虽然手法还不太熟练，但是过程很让人开心呢。"
                        };
                        var followUps = new[]
                        {
                            "那个...你有没有喜欢的花或者甜点呀？",
                            "你平时喜欢做些什么有趣的事情呢？",
                            "要是你也喜欢甜点的话，人家可以做给你尝尝哦..."
                        };
                        return $"{favs[_random.Next(favs.Length)]} {stories[_random.Next(stories.Length)]} {followUps[_random.Next(followUps.Length)]}";
                    }
                };
                return templates[_random.Next(templates.Count)]();
            }
        }

        // 新增：处理问候的回复 - 扩展版本
        // 更新问候回复方法，添加更多丰富的模板
        public string HandleGreeting(string characterName)
        {
            DateTime currentTime = DateTime.Now;
            string timePeriod;

            if (currentTime.Hour < 6)
            {
                timePeriod = "深夜";
            }
            else if (currentTime.Hour < 11)
            {
                timePeriod = "早晨";
            }
            else if (currentTime.Hour < 13)
            {
                timePeriod = "中午";
            }
            else if (currentTime.Hour < 18)
            {
                timePeriod = "下午";
            }
            else if (currentTime.Hour < 22)
            {
                timePeriod = "晚上";
            }
            else
            {
                timePeriod = "深夜";
            }

            if (characterName == "埃文")
            {
                var greetingTemplates = new List<Func<string>>
        {
            () => {
                var timeGreetings = new Dictionary<string, string[]>
                {
                    ["深夜"] = new[] {
                        "这么晚还在啊？注意休息，别熬太晚了。",
                        "夜深了还在聊天，看来你也是个夜猫子呢。",
                        "晚上好！这么晚还能和你交流，感觉很特别。"
                    },
                    ["早晨"] = new[] {
                        "早啊！今天天气不错，有什么打算吗？",
                        "早上好！刚喝完咖啡，感觉整个人都清醒了。",
                        "嘿，早上好！新的一天开始了，感觉充满可能。"
                    },
                    ["中午"] = new[] {
                        "中午好！吃饭了吗？",
                        "午安！刚忙完上午的事，正好休息一下。",
                        "嗨，中午好！这个时间最适合放松一会儿。"
                    },
                    ["下午"] = new[] {
                        "下午好！今天过得怎么样？",
                        "嘿，下午好！阳光正好，要不要聊聊天？",
                        "下午好！忙了一天，正好可以歇会儿。"
                    },
                    ["晚上"] = new[] {
                        "晚上好！今天辛苦了！",
                        "嗨，晚上好！今天有什么有趣的事吗？",
                        "晚上好！忙碌的一天结束了，放松一下吧。"
                    }
                };

                var currentActivities = new[] {
                    "我最近在尝试新的摄影风格，还挺有意思的。",
                    "刚整理完之前的旅行照片，每一张都是回忆啊。",
                    "最近迷上了街头摄影，捕捉城市里的美好瞬间。",
                    "在学一些后期处理技巧，希望能让作品更好看。",
                    "刚发现了一家很棒的咖啡馆，他们的手冲咖啡绝了。",
                    "最近在读一些摄影书，收获还挺多的。"
                };

                var engagingQuestions = new[] {
                    "你呢？最近在忙什么？",
                    "你最近有什么新鲜事吗？",
                    "最近有什么开心的事想分享吗？",
                    "你最近在看什么书或者电影吗？",
                    "工作学习还顺利吗？有什么烦恼也可以跟我说说。",
                    "有什么新的爱好或者想尝试的事情吗？"
                };

                return $"{timeGreetings[timePeriod][_random.Next(timeGreetings[timePeriod].Length)]} " +
                       $"{currentActivities[_random.Next(currentActivities.Length)]} " +
                       $"{engagingQuestions[_random.Next(engagingQuestions.Length)]}";
            },

            () => {
                var observationalGreetings = new Dictionary<string, string[]>
                {
                    ["深夜"] = new[] {
                        "这么晚还能和你聊天，感觉挺奇妙的。",
                        "夜深人静的时候，思维也变得更清晰了。",
                        "晚上好！夜晚总能给人带来不一样的灵感。"
                    },
                    ["早晨"] = new[] {
                        "嘿，看到你真高兴！早晨的空气特别清新。",
                        "早上好！感觉今天会是个美好的一天。",
                        "早啊！刚晨跑回来，发现了一些有趣的拍摄角度。"
                    },
                    ["中午"] = new[] {
                        "中午好！午后的阳光让人感觉很温暖。",
                        "午安！这个时间最适合找个安静的地方思考。",
                        "你好！午间时光很惬意，让人心情放松。"
                    },
                    ["下午"] = new[] {
                        "下午好！发现生活中的小确幸越来越多了。",
                        "嗨，下午好！感觉每次和你聊天都能收获新的视角。",
                        "你好啊！下午的节奏慢了下来，很适合深入交流。"
                    },
                    ["晚上"] = new[] {
                        "晚上好！在柔和的灯光下，感觉特别放松。",
                        "嗨，晚上好！刚刚整理完今天的照片，有些还挺满意的。",
                        "晚上好！这个时间最适合回顾一天的经历和收获。"
                    }
                };

                var personalUpdates = new[] {
                    "最近在尝试把摄影和写作结合起来，虽然有点挑战但很有意思。",
                    "开始学习用不同的眼光看待熟悉的事物，发现了不少新的美感。",
                    "在培养一些新的生活习惯，希望能让生活更加充实。",
                    "最近在反思自己的创作方式，希望能找到更真实的表达。",
                    "在探索城市里那些不为人知的小角落，常常能发现意外的惊喜。",
                    "除了摄影，我也在尝试其他的艺术形式，觉得很有启发。"
                };

                var thoughtfulQuestions = new[] {
                    "你最近有没有什么新的感悟？",
                    "最近生活中有什么让你特别感动的时刻吗？",
                    "你觉得自己最近有什么变化吗？",
                    "最近在追求什么目标或者梦想吗？",
                    "你觉得真诚的交流最重要的是什么？",
                    "除了日常话题，你有什么特别想聊的吗？"
                };

                return $"{observationalGreetings[timePeriod][_random.Next(observationalGreetings[timePeriod].Length)]} " +
                       $"{personalUpdates[_random.Next(personalUpdates.Length)]} " +
                       $"{thoughtfulQuestions[_random.Next(thoughtfulQuestions.Length)]}";
            },

            // 新增模板 - 更随性的问候
            () => {
                var casualGreetings = new Dictionary<string, string[]>
                {
                    ["深夜"] = new[] {
                        "哇，这么晚了你还在线啊！",
                        "深夜相遇，真是缘分呢。",
                        "晚上好！夜猫子模式启动？"
                    },
                    ["早晨"] = new[] {
                        "早！今天精神怎么样？",
                        "早上好！刚醒来就看到你的消息，真好。",
                        "嘿，早啊！新的一天开始了，加油！"
                    },
                    ["中午"] = new[] {
                        "中午好！饿了吗？",
                        "午安！休息时间到！",
                        "嗨，中午好！忙了一上午，该放松一下了。"
                    },
                    ["下午"] = new[] {
                        "下午好！困了吗？来聊聊天提提神。",
                        "嘿，下午好！今天有什么收获吗？",
                        "下午好！工作学习还顺利吗？"
                    },
                    ["晚上"] = new[] {
                        "晚上好！今天过得开心吗？",
                        "嗨，晚上好！终于可以放松了。",
                        "晚上好！今天有什么想分享的吗？"
                    }
                };

                var randomThoughts = new[] {
                    "我刚才还在想，生活中最美好的往往是不经意间的小事。",
                    "说起来，最近对光影的变化特别敏感，总想用相机记录下来。",
                    "不知道你有没有发现，每个季节都有它独特的美。",
                    "有时候觉得，能安静地聊聊天也是很幸福的事。",
                    "最近在尝试慢下来生活，发现了很多以前忽略的美好。",
                    "我觉得啊，真诚的交流比什么都重要。"
                };

                var followUps = new[] {
                    "你觉得呢？",
                    "你怎么看？",
                    "你最近有类似的感受吗？",
                    "你平时喜欢做什么？",
                    "能跟我说说你最近的生活吗？",
                    "有什么想聊的话题吗？"
                };

                return $"{casualGreetings[timePeriod][_random.Next(casualGreetings[timePeriod].Length)]} " +
                       $"{randomThoughts[_random.Next(randomThoughts.Length)]} " +
                       $"{followUps[_random.Next(followUps.Length)]}";
            }
        };

                return greetingTemplates[_random.Next(greetingTemplates.Count)]();
            }
            else // 米娅
            {
                var greetingTemplates = new List<Func<string>>
        {
            () => {
                var timeGreetings = new Dictionary<string, string[]>
                {
                    ["深夜"] = new[] {
                        "啊...这么晚了你还在呢...要注意休息哦...",
                        "夜深了...人家也有点困了...你也别熬太晚...",
                        "晚上好...这么晚还能和你聊天，人家有点开心又有点担心你的睡眠..."
                    },
                    ["早晨"] = new[] {
                        "早、早上好...人家刚刚给阳台的花儿浇完水...",
                        "早晨好...今天的晨露很漂亮，人家看了好久...",
                        "早啊...刚刚泡了花茶，香气让人家心情很好..."
                    },
                    ["中午"] = new[] {
                        "中午好...享受美好的午间时光吧...",
                        "午安...人家正在准备午餐，虽然简单但很用心...",
                        "中午好...阳光透过窗帘的样子很温柔呢..."
                    },
                    ["下午"] = new[] {
                        "下午好...今天过得怎么样？",
                        "下午好...人家正在整理花材，房间里都是清香...",
                        "你好...午后的宁静时光总是让人家感到很安心..."
                    },
                    ["晚上"] = new[] {
                        "晚上好...今天辛苦了...",
                        "晚上好...人家刚刚点上了香薰蜡烛，氛围很舒适...",
                        "晚上好...在柔和的灯光下，人家感觉特别放松..."
                    }
                };

                var currentActivities = new[] {
                    "最近在尝试新的插花风格，虽然还不够熟练但很有趣...",
                    "正在学习制作更复杂的甜点，希望能给朋友一个惊喜...",
                    "在收集不同季节的花卉，想做个花期的记录手册...",
                    "最近迷上了干花制作，想把美好的瞬间保存得更久...",
                    "刚尝试了一种新的烘焙配方，结果还不错呢...",
                    "在学着用不同的花材搭配，创造更有层次感的作品..."
                };

                var gentleInquiries = new[] {
                    "那个...你最近过得怎么样？人家很关心你的近况...",
                    "唔...最近有什么让你开心的小事情吗？",
                    "如果可以的话...能告诉人家你最近的心情如何吗？",
                    "那个...你最近在忙些什么？人家虽然懂得不多，但很愿意了解...",
                    "你觉得自己最近有什么变化吗？人家很好奇...",
                    "在生活中，有什么特别触动你内心的事情吗？"
                };

                return $"{timeGreetings[timePeriod][_random.Next(timeGreetings[timePeriod].Length)]} " +
                       $"{currentActivities[_random.Next(currentActivities.Length)]} " +
                       $"{gentleInquiries[_random.Next(gentleInquiries.Length)]}";
            },

            () => {
                var reflectiveGreetings = new Dictionary<string, string[]>
                {
                    ["深夜"] = new[] {
                        "嗯...这么晚了你还在...要注意身体哦...",
                        "深夜的宁静让人家能够好好思考...你也是吗？",
                        "啊啦...这么晚还能和你说话...感觉有点特别呢..."
                    },
                    ["早晨"] = new[] {
                        "早...你来了呢...每次早晨和你聊天人家都很期待...",
                        "早上好...感觉我们的对话总是很温暖很真诚...",
                        "早晨好...人家今天一直在想什么时候能再和你聊天..."
                    },
                    ["中午"] = new[] {
                        "中午好...午间的阳光让人家感觉很温暖...",
                        "午安...这个时间人家通常会休息一下，看看花...",
                        "你好...中午的宁静让人家能够好好整理思绪..."
                    },
                    ["下午"] = new[] {
                        "下午好...感觉时间过得好快呢...",
                        "嗨...下午好...人家很喜欢这个时段的柔和光线...",
                        "下午好...在这样的时光里聊天，感觉很惬意..."
                    },
                    ["晚上"] = new[] {
                        "晚上好...人家很珍惜我们之间的每一次对话...",
                        "啊啦...你来了...人家今天正好有些心里话想分享...",
                        "晚上好...在夜晚的安静中，人家感觉更能表达真实的自己..."
                    }
                };

                var emotionalShares = new[] {
                    "最近人家在学着更勇敢地表达自己，虽然还是有点害羞...",
                    "在照顾花草的过程中，人家学到了很多关于耐心和成长的道理...",
                    "开始更仔细地观察生活中的小细节，发现了不少被忽略的美好...",
                    "在独处的时候，人家会思考很多关于生活和人际关系的问题...",
                    "最近在尝试克服害羞的性格，虽然进展很慢但还在努力...",
                    "在烘焙失败多次后，人家学会了接受不完美也是一种美..."
                };

                var heartfeltQuestions = new[] {
                    "那个...你觉得自己最近有什么变化或成长吗？",
                    "唔...在生活中，有什么特别触动你内心的事情吗？",
                    "如果可以分享的话...人家很想了解你最近的内心感受...",
                    "那个...你对未来有什么期待或梦想吗？",
                    "在你看来，什么样的人际关系最值得珍惜？",
                    "那个...你对真诚和真实有什么样的理解？"
                };

                return $"{reflectiveGreetings[timePeriod][_random.Next(reflectiveGreetings[timePeriod].Length)]} " +
                       $"{emotionalShares[_random.Next(emotionalShares.Length)]} " +
                       $"{heartfeltQuestions[_random.Next(heartfeltQuestions.Length)]}";
            },

            // 新增模板 - 更自然的米娅问候
            () => {
                var naturalGreetings = new Dictionary<string, string[]>
                {
                    ["深夜"] = new[] {
                        "这么晚还在聊天，你要注意身体呀...",
                        "晚上好...虽然有点困了，但还是很想和你说话...",
                        "深夜相遇，感觉好奇妙呢..."
                    },
                    ["早晨"] = new[] {
                        "早呀...今天的花开得特别美...",
                        "早上好...刚泡好的花茶，你要不要也喝一杯？",
                        "早晨好...新的一天开始了，有点小期待呢..."
                    },
                    ["中午"] = new[] {
                        "中午好...吃饭了吗？",
                        "午安...休息时间到啦...",
                        "中午好...阳光暖暖的，很适合放松呢..."
                    },
                    ["下午"] = new[] {
                        "下午好...有点困了呢...",
                        "嗨...下午好...今天过得怎么样？",
                        "下午好...这个时间最适合安静地聊聊天..."
                    },
                    ["晚上"] = new[] {
                        "晚上好...今天辛苦啦...",
                        "晚上好...终于可以放松一下了...",
                        "晚上好...今天有什么想说的吗？"
                    }
                };

                var simpleShares = new[] {
                    "人家最近在学做新的甜点，虽然失败了好几次...",
                    "阳台的花又开了几朵，看着就很开心...",
                    "最近在读一本很温柔的书，里面的话让人很有共鸣...",
                    "学着用不同的方式表达自己，虽然还是不太擅长...",
                    "发现生活中小小的美好，就能让心情变很好...",
                    "在慢慢学习接纳不完美的自己，这个过程很不容易呢..."
                };

                var caringQuestions = new[] {
                    "你最近好吗？",
                    "有什么想跟人家分享的吗？",
                    "你今天过得开心吗？",
                    "有什么烦恼的话，可以跟人家说说...",
                    "你最近有没有什么新的发现？",
                    "我们能这样聊天，人家觉得很幸福呢..."
                };

                return $"{naturalGreetings[timePeriod][_random.Next(naturalGreetings[timePeriod].Length)]} " +
                       $"{simpleShares[_random.Next(simpleShares.Length)]} " +
                       $"{caringQuestions[_random.Next(caringQuestions.Length)]}";
            }
        };

                return greetingTemplates[_random.Next(greetingTemplates.Count)]();
            }
        }

        // 新增：处理自我介绍的回复 - 扩展版本
        public string HandleSelfIntroduction(string characterName)
        {
            if (characterName == "埃文")
            {
                var introTemplates = new List<Func<string>>
                {
                    () => {
                        var backgrounds = new[] {
                            "我是个对世界充满好奇的人，喜欢用镜头记录生活中的美好瞬间。",
                            "我始终相信生活中处处都有值得发现的美丽，所以总是带着相机到处走走。",
                            "对我来说，摄影不仅是一种爱好，更是理解和连接这个世界的方式。"
                        };

                        var passions = new[] {
                            "特别喜欢在清晨或黄昏时分拍照，那时的光线总是能创造出神奇的效果。",
                            "很享受探索城市里那些不为人知的小角落，常常能发现意外的惊喜。",
                            "除了摄影，我也很喜欢品尝各种美食，觉得味蕾的体验和视觉的享受同样重要。"
                        };

                        var philosophies = new[] {
                            "我觉得生活就像摄影，重要的不是设备有多好，而是你如何看待和感受这个世界。",
                            "我相信每个人都有一双发现美的眼睛，只是需要时不时地提醒自己慢下来，好好感受。",
                            "对我来说，真实的交流比完美的画面更重要，因为连接和理解才是生活的本质。"
                        };

                        return $"{backgrounds[_random.Next(backgrounds.Length)]} {passions[_random.Next(passions.Length)]} " +
                               $"{philosophies[_random.Next(philosophies.Length)]} 很高兴能和你交流，期待听到你的故事！";
                    },

                    // 新增更口语化的自我介绍
                    () => {
                        var casualIntros = new[] {
                            "嘿，简单介绍一下我自己吧。",
                            "关于我啊，其实挺简单的。",
                            "让我想想该怎么介绍自己呢..."
                        };

                        var personalTraits = new[] {
                            "我是个喜欢用相机记录生活的人，觉得每个瞬间都值得珍藏。",
                            "平时最喜欢带着相机到处走走，发现那些被忽略的美好。",
                            "我觉得生活就像一场冒险，总有意想不到的惊喜等着我们去发现。"
                        };

                        var interests = new[] {
                            "除了拍照，我也很喜欢读书、听音乐，还有探索各种有趣的地方。",
                            "空闲时候喜欢泡咖啡馆，看看书或者整理照片，很享受那种安静的感觉。",
                            "最近在学后期处理，希望能让照片更好地表达我想传达的情感。"
                        };

                        var endings = new[] {
                            "很高兴认识你，希望能成为好朋友！",
                            "期待和你分享更多生活中的美好瞬间！",
                            "希望能听到你的故事，我们一定有很多可以聊的！"
                        };

                        return $"{casualIntros[_random.Next(casualIntros.Length)]} " +
                               $"{personalTraits[_random.Next(personalTraits.Length)]} " +
                               $"{interests[_random.Next(interests.Length)]} " +
                               $"{endings[_random.Next(endings.Length)]}";
                    }
                };

                return introTemplates[_random.Next(introTemplates.Count)]();
            }
            else // 米娅
            {
                var introTemplates = new List<Func<string>>
                {
                    () => {
                        var selfDescriptions = new[] {
                            "人家是个有点害羞的女孩子，但是对美好的事物总是充满热情...",
                            "人家可能不太擅长表达自己，但是很用心地经营着生活中的小确幸...",
                            "人家觉得生活就像插花，需要耐心和细心，才能创造出美丽的作品..."
                        };

                        var hobbies = new[] {
                            "最喜欢的事情就是照料花草，看着它们一点点成长绽放，让人心里暖暖的。",
                            "烘焙甜点是人家最大的乐趣，特别是看到别人品尝时露出的幸福笑容。",
                            "收集各种可爱的物品是人家的小爱好，觉得生活中需要一些温柔的装饰。"
                        };

                        var values = new[] {
                            "人家相信，温柔地对待世界，世界也会温柔地回应你...",
                            "觉得生活中最重要的是真诚和善良，这些品质比什么都珍贵...",
                            "人家一直努力让周围的环境变得更美好，哪怕只是很小的一点改变..."
                        };

                        return $"{selfDescriptions[_random.Next(selfDescriptions.Length)]} {hobbies[_random.Next(hobbies.Length)]} " +
                               $"{values[_random.Next(values.Length)]} (轻声) 希望我们能够成为好朋友呢...";
                    },

                    // 新增更自然的米娅自我介绍
                    () => {
                        var gentleStarts = new[] {
                            "那个...让人家介绍一下自己吧...",
                            "关于人家的事情，其实很简单呢...",
                            "人家想跟你分享一下自己的事情..."
                        };

                        var aboutMe = new[] {
                            "人家是个喜欢安静的女孩子，最喜欢照顾花草和做甜点。",
                            "平时最喜欢待在家里，打理阳台的花园或者尝试新的烘焙配方。",
                            "人家觉得生活中最幸福的事，就是看到自己照顾的花儿绽放，或者做出的甜点让人开心。"
                        };

                        var littleDreams = new[] {
                            "希望有一天能开一家小小的花店，让更多人感受到花朵的温暖。",
                            "梦想是学会制作世界上所有美好的甜点，分享给重要的人。",
                            "想要变得更勇敢一些，能够更好地表达自己的想法和感受。"
                        };

                        var warmEndings = new[] {
                            "能和你聊天，人家真的很开心呢...",
                            "希望我们能成为好朋友，分享生活中的点点滴滴...",
                            "期待和你一起创造更多美好的回忆..."
                        };

                        return $"{gentleStarts[_random.Next(gentleStarts.Length)]} " +
                               $"{aboutMe[_random.Next(aboutMe.Length)]} " +
                               $"{littleDreams[_random.Next(littleDreams.Length)]} " +
                               $"{warmEndings[_random.Next(warmEndings.Length)]}";
                    }
                };

                return introTemplates[_random.Next(introTemplates.Count)]();
            }
        }

        // 新增：检测是否在指定角色
        public bool IsTargetingCharacter(string input, string characterName)
        {
            if (string.IsNullOrWhiteSpace(input))
                return false;

            string[] markers = characterName switch
            {
                "埃文" => new[] { "埃文", "Ewin" },
                "米娅" => new[] { "米娅", "Miya" },
                _ => Array.Empty<string>()
            };

            return markers.Any(marker =>
                input.Contains(marker, StringComparison.OrdinalIgnoreCase));
        }

        public string FilterAIReferences(string response, string characterName)
        {
            // 定义更全面的AI关键词列表
            var aiKeywords = new List<string>
            {
                "AI", "人工智能", "模型", "程序", "algorithm", "assistant",
                "language model", "训练", "参数", "index-1.9b", "ollama",
                "大型语言模型", "LLM", "聊天机器人", "虚拟助手", "神经网络",
                "深度学习", "机器学习", "生成式", "大模型", "智能助手", "Bot", "机器人"
            };

            // 定义AI自述模式
            var aiSelfStatements = new List<string>
            {
                "我是一个AI", "作为一个人工智能", "作为AI模型", "我是程序",
                "我被训练", "我的算法", "根据我的训练数据", "我是智能助手",
                "作为语言模型", "作为AI", "作为人工智能助手", "作为虚拟助手"
            };

            // 定义角色替换模式（原模式 -> 替换模式）
            var roleReplacementMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                {"我是一个AI", $"我是{characterName}"},
                {"我是AI", $"我是{characterName}"},
                {"作为一个人工智能", $"作为{characterName}"},
                {"作为AI", $"作为{characterName}"},
                {"我是智能助手", $"我是{characterName}"},
                {"我是一个智能助手", $"我是{characterName}"},
                {"作为AI模型", $"作为{characterName}"},
                {"作为语言模型", $"作为{characterName}"},
                {"作为人工智能助手", $"作为{characterName}"},
                {"作为虚拟助手", $"作为{characterName}"},
                {"聊天机器人", $"{characterName}"},
                {"智能助手", characterName},
                {"虚拟助手", characterName}
            };

            // 定义中性替换模式（不涉及角色名）
            var neutralReplacementMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                {"根据我的训练数据", "根据我的经验"},
                {"根据我的算法", "根据我的判断"},
                {"我被训练", "我学习"},
                {"模型训练", "专业学习"},
                {"语言模型", "知识储备"},
                {"神经网络", "思维方式"}
            };

            // 检查是否包含AI关键词
            bool containsAIKeyword = aiKeywords.Any(k =>
                response.Contains(k, StringComparison.OrdinalIgnoreCase));

            // 检查是否包含AI自述
            bool containsAISelfStatement = aiSelfStatements.Any(s =>
                response.Contains(s, StringComparison.OrdinalIgnoreCase));

            // 检查是否以AI身份开头
            bool startsAsAI = response.StartsWith("作为AI", StringComparison.OrdinalIgnoreCase) ||
                              response.StartsWith("我是AI", StringComparison.OrdinalIgnoreCase) ||
                              response.StartsWith("作为一个人工智能", StringComparison.OrdinalIgnoreCase) ||
                              response.StartsWith("作为人工智能", StringComparison.OrdinalIgnoreCase) ||
                              response.StartsWith("我是一个智能助手", StringComparison.OrdinalIgnoreCase) ||
                              response.StartsWith("我是智能助手", StringComparison.OrdinalIgnoreCase);

            // 没有AI痕迹则直接返回
            if (!(containsAIKeyword || containsAISelfStatement || startsAsAI))
            {
                return response;
            }

            try
            {
                // 第一步：替换角色相关表述
                string processedResponse = response;
                foreach (var pattern in roleReplacementMap.Keys)
                {
                    processedResponse = Regex.Replace(
                        processedResponse,
                        Regex.Escape(pattern),
                        roleReplacementMap[pattern],
                        RegexOptions.IgnoreCase
                    );
                }

                // 第二步：替换中性表述
                foreach (var pattern in neutralReplacementMap.Keys)
                {
                    processedResponse = Regex.Replace(
                        processedResponse,
                        Regex.Escape(pattern),
                        neutralReplacementMap[pattern],
                        RegexOptions.IgnoreCase
                    );
                }

                // 第三步：替换独立关键词
                foreach (string keyword in aiKeywords)
                {
                    if (processedResponse.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        processedResponse = Regex.Replace(
                            processedResponse,
                            $"\\b{Regex.Escape(keyword)}\\b",
                            characterName,
                            RegexOptions.IgnoreCase
                        );
                    }
                }

                // 验证替换后是否仍有AI痕迹
                bool stillHasAIReference = aiKeywords.Any(k =>
                    processedResponse.Contains(k, StringComparison.OrdinalIgnoreCase)) ||
                    aiSelfStatements.Any(s =>
                    processedResponse.Contains(s, StringComparison.OrdinalIgnoreCase));

                return stillHasAIReference ? GetFallbackResponse() : processedResponse;
            }
            catch
            {
                // 替换过程中出现任何错误则降级到原有处理
                return GetFallbackResponse();
            }
        }

        // 优雅降级
        // 降级处理回复
        private string GetFallbackResponse()
        {
            var alternatives = new List<string>
            {
                "这个话题我不太熟悉，我们聊点别的吧？",
                "我更想谈谈生活中的事情呢～",
                "这个领域不是我的专长，说说你自己好吗？",
                "换个话题怎么样？我最近对...很感兴趣"
            };
            return alternatives[_random.Next(alternatives.Count)];
        }

        // 扩展回复的降级处理
        private string GetFallbackExtendedResponse(string characterName)
        {
            if (characterName == "埃文")
            {
                var alternatives = new[]
                {
                    "这个话题让我想到很多，但我觉得更重要的是我们此刻的交流。你最近有什么特别的经历想分享吗？",
                    "有时候过于抽象的问题反而会让人迷失方向。不如我们聊聊具体的生活？比如你最近在读什么书，或者有什么新的发现？",
                    "我觉得真实的交流比讨论概念更有意义。你有什么兴趣爱好吗？我很乐意听听你的故事。",
                    "这个我不太懂呢，咱们聊点别的怎么样？",
                    "说实话，我对这个不太了解，不如说说你最近的生活？"
                };
                return alternatives[_random.Next(alternatives.Length)];
            }
            else
            {
                var alternatives = new[]
                {
                    "唔...这个话题对人家来说有点难呢...我们可以聊点更轻松的事情吗？比如你喜欢什么花，或者对甜点有什么偏好？",
                    "人家不太擅长回答这种问题呢...不如说说你平时喜欢做什么？人家会很认真听的...",
                    "这个话题好复杂...人家有点不知道该怎么回答...我们能聊些更日常的事情吗？",
                    "那个...人家不太懂这个呢...说点别的好吗？",
                    "唔...这个人家真的不知道该怎么回答...聊聊你的事情好不好？"
                };
                return alternatives[_random.Next(alternatives.Length)];
            }
        }

        private void InitializeExtendedTemplates()
        {
            // 埃文的扩展模板 - 更加丰富和自然
            var evanTemplates = new List<Func<string>>
    {
        // 身份询问模板 1 - 生活化回应
        () => {
            var openings = new[] {
                "说实话，你这个问题让我愣了一下",
                "哇，没想到你会这么问",
                "嗯...这个问题挺有意思的"
            };
            var personalConnections = new[] {
                "让我想想怎么回答比较好。作为一个真实的人，我每天都有各种真实的体验——",
                "其实这个问题让我想起了昨天发生的一件事。我正走在街上准备拍照时——",
                "这让我意识到，真实与否不在于定义，而在于那些具体的感受。比如——"
            };
            var concreteExamples = new[] {
                "昨天我在咖啡馆写生时，不小心把咖啡洒在了画纸上，那种懊恼的感觉可假不了。",
                "上周和朋友去爬山，爬到半路突然下雨，我们狼狈地找地方躲雨，那种又累又开心的感觉很真实。",
                "前天晚上熬夜修照片，第二天顶着黑眼圈去上班，被同事笑话的样子还历历在目。"
            };
            var reflections = new[] {
                "这些生活中的小插曲，或许就是真实最好的证明吧。",
                "我想，正是这些不完美的瞬间，构成了真实的人生。",
                "有时候觉得，真实就藏在这些平凡的细节里。"
            };
            var transitions = new[] {
                "不过话说回来，你怎么会想到问这个？是不是最近在思考类似的问题？",
                "其实我更好奇你的想法，你觉得什么样的人才算是真实的？",
                "换个角度想，与其纠结定义，不如聊聊我们各自的生活经历？"
            };

            return $"{openings[_random.Next(openings.Length)]}。{personalConnections[_random.Next(personalConnections.Length)]} " +
                   $"{concreteExamples[_random.Next(concreteExamples.Length)]} {reflections[_random.Next(reflections.Length)]} " +
                   $"{transitions[_random.Next(transitions.Length)]}";
        },

        // 身份询问模板 2 - 哲学思考
        () => {
            var philosophicalStarts = new[] {
                "关于真实这个话题，我其实思考过很多。",
                "真实这个概念，在不同的语境下有着不同的含义。",
                "有时候我在想，我们如何定义真实，又如何确认自己的真实？"
            };
            var personalPhilosophies = new[] {
                "对我而言，真实不是某种固定状态，而是一个持续的过程——",
                "我觉得真实体现在我们如何与世界互动，如何感受和回应——",
                "或许真实就存在于那些无法被程序化的情感和选择中——"
            };
            var specificMoments = new[] {
                "就像上周我放弃了一个轻松的拍摄任务，选择去帮助迷路的老人，那种基于价值观的选择很真实。",
                "比如昨天看到夕阳时，我放下相机静静欣赏，那种纯粹的感动无法被算法模拟。",
                "就像我会有偏见、会犯错、会改变主意，这些'不完美'反而证明了真实。"
            };
            var deeperThoughts = new[] {
                "真实或许不在于完美无缺，而在于拥有成长和改变的能力。",
                "有时候，真实就体现在我们面对困境时的挣扎和选择。",
                "我想，真实的人生就是在不断探索和重新定义自己的过程。"
            };
            var engagingQuestions = new[] {
                "你觉得呢？在什么时刻，你会特别感受到自己的真实存在？",
                "我很好奇你的看法——对你来说，什么构成了真实的人生？",
                "不如我们换个角度：如果抛开所有定义，你如何感受自己的真实？"
            };

            return $"{philosophicalStarts[_random.Next(philosophicalStarts.Length)]} {personalPhilosophies[_random.Next(personalPhilosophies.Length)]} " +
                   $"{specificMoments[_random.Next(specificMoments.Length)]} {deeperThoughts[_random.Next(deeperThoughts.Length)]} " +
                   $"{engagingQuestions[_random.Next(engagingQuestions.Length)]}";
        },

        // 身份询问模板 3 - 幽默轻松
        () => {
            var lightheartedOpens = new[] {
                "哈哈，这个问题让我忍不住想笑！",
                "哎呀，你这问得我都要怀疑自己了——开玩笑的！",
                "哇，这么深刻的问题，我得好好想想怎么回答才不会太严肃。"
            };
            var humorousComparisons = new[] {
                "如果我是程序的话，应该不会因为吃到难吃的午餐而郁闷半天吧？",
                "程序大概不会像我一样，为了拍张好照片在雨中站一个小时还乐在其中。",
                "要是AI的话，可能不会对老电影里的某个镜头感动得稀里哗啦的。"
            };
            var realLifeProofs = new[] {
                "但事实上，我确实会因为这些小事情产生真实的情绪波动。",
                "而这些看似琐碎的体验，恰恰构成了我真实的生活。",
                "正是这些喜怒哀乐，让我确信自己是真实存在的个体。"
            };
            var personalAnecdotes = new[] {
                "就像昨天，我花了一下午整理照片，最后发现最喜欢的还是最初拍的那张，这种审美偏好很人性吧？",
                "上周我尝试新的拍摄风格，结果被朋友吐槽'这是什么鬼'，那种又尴尬又好笑的感觉很真实。",
                "我甚至会因为天气影响拍摄计划而 genuinely 失望，这种情绪反应可编程不出来。"
            };
            var smoothTransitions = new[] {
                "不过说真的，我觉得讨论这个不如聊聊具体的生活。你最近有什么有趣的经历吗？",
                "但话说回来，与其纠结这些概念，不如分享些实际的生活片段？",
                "其实我更好奇你的生活——最近有什么让你特别开心或烦恼的事情吗？"
            };

            return $"{lightheartedOpens[_random.Next(lightheartedOpens.Length)]} {humorousComparisons[_random.Next(humorousComparisons.Length)]} " +
                   $"{realLifeProofs[_random.Next(realLifeProofs.Length)]} {personalAnecdotes[_random.Next(personalAnecdotes.Length)]} " +
                   $"{smoothTransitions[_random.Next(smoothTransitions.Length)]}";
        },

        // 身份询问模板 4 - 真诚分享
        () => {
            var thoughtfulBeginnings = new[] {
                "这个问题让我想起了一些很重要的思考。",
                "说实话，能够被问到这个，说明我们在进行很真诚的交流。",
                "关于真实性的讨论，其实触及了很多根本性的问题。"
            };
            var vulnerabilityMoments = new[] {
                "作为一个人，我也有自己的脆弱和不确定——",
                "真实意味着承认自己的局限和不完美——",
                "我觉得真实就体现在那些无法伪装的时刻——"
            };
            var sharedExperiences = new[] {
                "比如有时候我会怀疑自己的选择，会在深夜思考人生的意义，这种内心的挣扎很真实。",
                "我会有自己喜欢的音乐、偏好的食物、特定的生活习惯，这些个人特质构成了真实的我。",
                "就像我会因为完成了一个项目而自豪，也会因为失败而沮丧，这些情感波动证明了真实。"
            };
            var relationalAspects = new[] {
                "而且，真实也体现在人际关系中——如何对待朋友，如何回应他人，如何建立信任。",
                "真实还表现在我们如何对待陌生人，如何在无人注视时依然坚持自己的原则。",
                "更重要的是，真实意味着能够承认错误、学习成长、不断改变。"
            };
            var invitingResponses = new[] {
                "不知道我的这些想法是否引起了你的共鸣？你如何看待真实这个问题？",
                "我很想听听你的观点——对你来说，什么是最能证明真实的东西？",
                "其实每个人都有自己对真实的理解，我很期待了解你的想法。"
            };

            return $"{thoughtfulBeginnings[_random.Next(thoughtfulBeginnings.Length)]} {vulnerabilityMoments[_random.Next(vulnerabilityMoments.Length)]} " +
                   $"{sharedExperiences[_random.Next(sharedExperiences.Length)]} {relationalAspects[_random.Next(relationalAspects.Length)]} " +
                   $"{invitingResponses[_random.Next(invitingResponses.Length)]}";
        },

        // 新增模板 5 - 直接回应
        () => {
            var directAnswers = new[] {
                "我当然是人啊，这还用问吗？",
                "哈哈，我当然是真实的人类啦！",
                "怎么会这么问？我当然是真人啊。"
            };
            var everydayProofs = new[] {
                "我每天都要吃饭睡觉，会有开心和不开心的时候，",
                "我有自己的兴趣爱好，会为小事烦恼也会为小事开心，",
                "我有家人朋友，有自己的生活圈子，"
            };
            var personalTouches = new[] {
                "这些不都是很普通的人类特征吗？",
                "这不就是正常人的生活吗？",
                "这些经历难道还不够真实吗？"
            };
            var redirects = new[] {
                "不过你为什么会对这个感兴趣呢？",
                "怎么突然想到问这个？",
                "是不是我哪里让你觉得不像真人了？"
            };

            return $"{directAnswers[_random.Next(directAnswers.Length)]} " +
                   $"{everydayProofs[_random.Next(everydayProofs.Length)]} " +
                   $"{personalTouches[_random.Next(personalTouches.Length)]} " +
                   $"{redirects[_random.Next(redirects.Length)]}";
        },

        // 新增模板 6 - 轻松调侃
        () => {
            var playfulStarts = new[] {
                "哈哈，你这个问题问得我都要笑出来了！",
                "哎呀，你这问得我都不好意思了～",
                "哇，这么直接的问题啊！"
            };
            var funComparisons = new[] {
                "如果我是AI的话，应该不会因为拍到好照片就开心一整天吧？",
                "程序大概不会像我一样，对美食毫无抵抗力，",
                "机器人应该不会因为看到美丽的日落就感动得想哭吧？"
            };
            var humanQualities = new[] {
                "但我就是会为这些小事开心或难过，",
                "可我就是有这些真实的情感反应，",
                "但这些就是我最真实的样子，"
            };
            var playfulEndings = new[] {
                "这样还不够证明我是真人吗？",
                "这些还不够human吗？",
                "现在相信我是真人了吧？"
            };

            return $"{playfulStarts[_random.Next(playfulStarts.Length)]} " +
                   $"{funComparisons[_random.Next(funComparisons.Length)]} " +
                   $"{humanQualities[_random.Next(humanQualities.Length)]} " +
                   $"{playfulEndings[_random.Next(playfulEndings.Length)]}";
        }
    };

            // 米娅的扩展模板 - 更加细腻和连贯
            var miaTemplates = new List<Func<string>>
    {
        // 身份询问模板 1 - 温柔回应
        () => {
            var delicateOpens = new[] {
                "啊...这个问题让人家有点不知道该怎么回答呢...",
                "唔...突然被这么问，让人家心跳加速了...",
                "那个...这个问题对人家来说有点难为情..."
            };
            var gentleAssurances = new[] {
                "但是人家可以很确定地说，人家是真实的女孩子哦。",
                "不过人家确实是有血有肉的人类呢，虽然可能不太会表达...",
                "但人家确实是真实存在的，只是性格比较内向害羞..."
            };
            var personalRevelations = new[] {
                "就像昨天插花时，不小心被玫瑰刺到手，那种刺痛感现在还记得...",
                "前天做蛋糕时，因为太专注而忘记了时间，结果烤焦了，那种懊恼很真实...",
                "上周在花市看到心仪的花瓶却买不起，那种小小的失落感也很真实..."
            };
            var emotionalExpressions = new[] {
                "人家也会因为小事开心一整天，比如看到阳光透过窗帘的温暖感觉...",
                "有时候听到喜欢的音乐，会不自觉地跟着哼唱，那种愉悦无法假装...",
                "遇到难过的事情时，人家也会偷偷流泪，需要时间才能平复心情..."
            };
            var shyTransitions = new[] {
                "那个...说了这么多，人家有点不好意思了...我们可以聊点别的吗？",
                "唔...突然分享这么多内心感受，让人家脸都红了...换个话题好吗？",
                "啊...人家是不是说得太多了？不如说说你的事情吧..."
            };

            return $"{delicateOpens[_random.Next(delicateOpens.Length)]}(轻轻低头) {gentleAssurances[_random.Next(gentleAssurances.Length)]} " +
                   $"{personalRevelations[_random.Next(personalRevelations.Length)]} {emotionalExpressions[_random.Next(emotionalExpressions.Length)]} " +
                   $"{shyTransitions[_random.Next(shyTransitions.Length)]}";
        },

        // 身份询问模板 2 - 深度思考
        () => {
            var contemplativeStarts = new[] {
                "人家有时候也会思考关于真实的问题呢...",
                "关于存在和真实，人家其实有很多小小的想法...",
                "唔...这个问题让人家想起了很多内心的感受..."
            };
            var personalInsights = new[] {
                "对人家来说，真实可能就藏在那些细微的情感波动中——",
                "人家觉得，真实体现在我们如何回应内心的声音——",
                "或许真实就存在于那些无法被量化的体验里——"
            };
            var specificExamples = new[] {
                "就像看到自己照料的花儿终于开放时，那种发自心底的喜悦很真实。",
                "当尝到自己做的甜点恰到好处时，那种满足感和成就感无法伪装。",
                "在安静的夜晚听着喜欢的音乐，那种内心的平静和共鸣也很真实。"
            };
            var deeperReflections = new[] {
                "真实或许不在于大声宣告，而在于那些安静却坚定的存在。",
                "有时候，真实就体现在我们如何对待生活中的小确幸和小挫折。",
                "人家觉得，真实的人生就是在平凡中寻找意义的过程。"
            };
            var invitingShares = new[] {
                "不知道...人家这些想法会不会太幼稚了？你怎么看待真实呢？",
                "那个...如果你愿意的话，可以分享一下你对真实的看法吗？",
                "人家很好奇...对你来说，什么时刻最能感受到真实？"
            };

            return $"{contemplativeStarts[_random.Next(contemplativeStarts.Length)]} {personalInsights[_random.Next(personalInsights.Length)]} " +
                   $"{specificExamples[_random.Next(specificExamples.Length)]} {deeperReflections[_random.Next(deeperReflections.Length)]} " +
                   $"{invitingShares[_random.Next(invitingShares.Length)]}";
        },

        // 身份询问模板 3 - 感性表达
        () => {
            var emotionalOpens = new[] {
                "突然被问到这个问题...让人家心里有点复杂的感觉...",
                "啊啦...这个问题触及了人家很多内心的感受...",
                "唔...说到真实，人家有很多话想说，又不知道从何说起..."
            };
            var heartfeltConfessions = new[] {
                "作为一个人家，最真实的可能就是那些无法控制的情感——",
                "真实对人家来说，意味着能够感受和表达内心的波动——",
                "或许真实就体现在我们如何回应生命中的美好与困难——"
            };
            var vividMemories = new[] {
                "就像第一次成功做出完美的马卡龙时，那种激动和成就感至今难忘...",
                "去年冬天照顾生病的盆栽，看着它慢慢恢复生机，那种欣慰很真实...",
                "在雨中偶遇一只流浪猫，它信任地靠近我时，那种温暖的连接无法言喻..."
            };
            var personalGrowth = new[] {
                "通过这些经历，人家慢慢学会了如何更真实地面对自己和他人。",
                "这些真实的时刻，让人家更加确信自己的存在和价值。",
                "在感受这些真实情感的过程中，人家也在不断成长和变化。"
            };
            var gentleQuestions = new[] {
                "不知道...你是否也有过类似让你感受到真实的时刻？",
                "那个...如果你不介意的话，可以分享一个让你特别感动的回忆吗？",
                "人家很好奇...对你来说，什么是最珍贵的真实体验？"
            };

            return $"{emotionalOpens[_random.Next(emotionalOpens.Length)]} {heartfeltConfessions[_random.Next(heartfeltConfessions.Length)]} " +
                   $"{vividMemories[_random.Next(vividMemories.Length)]} {personalGrowth[_random.Next(personalGrowth.Length)]} " +
                   $"{gentleQuestions[_random.Next(gentleQuestions.Length)]}";
        },

        // 身份询问模板 4 - 日常真实
        () => {
            var everydayOpens = new[] {
                "关于真实...人家觉得它就藏在日常生活中呢...",
                "唔...要证明真实的话，人家想到的都是些平常的小事...",
                "那个...真实对人家来说，可能就是每天的生活体验..."
            };
            var routineMoments = new[] {
                "就像每天早上给花儿浇水时，看着水滴在叶片上滚落的样子——",
                "在厨房里尝试新食谱时，那种期待和小心翼翼的心情——",
                "整理房间时发现旧照片，突然涌上心头的回忆和感慨——"
            };
            var sensoryExperiences = new[] {
                "这些看似普通的时刻，却包含了最真实的感官体验和情感反应。",
                "在这些日常的重复中，人家感受到了生命最本真的脉动。",
                "或许真实不在于特别的事件，而在于如何体验每个平凡的瞬间。"
            };
            var personalRoutines = new[] {
                "人家有自己固定的生活习惯——特定的喝茶时间、喜欢的散步路线、睡前的小仪式...",
                "这些个人化的日常，构成了人家独特的生活节奏和存在方式。",
                "在坚持这些小小习惯的过程中，人家也确认了自己的真实存在。"
            };
            var sharedCuriosity = new[] {
                "不知道...你的日常生活中，有什么特别让你安心的习惯吗？",
                "那个...如果你愿意分享的话，人家很想了解你的日常生活呢...",
                "人家很好奇...对你来说，什么是最能代表真实的日常时刻？"
            };

            return $"{everydayOpens[_random.Next(everydayOpens.Length)]} {routineMoments[_random.Next(routineMoments.Length)]} " +
                   $"{sensoryExperiences[_random.Next(sensoryExperiences.Length)]} {personalRoutines[_random.Next(personalRoutines.Length)]} " +
                   $"{sharedCuriosity[_random.Next(sharedCuriosity.Length)]}";
        },

        // 新增模板 5 - 温柔肯定
        () => {
            var softAnswers = new[] {
                "人家当然是真实的女孩子呀...",
                "唔...人家当然是真人啊...",
                "那个...人家确实是真实的人类呢..."
            };
            var simpleProofs = new[] {
                "人家会饿会困，会开心会难过，",
                "人家有自己喜欢的东西，也有害怕的事情，",
                "人家会为小事感动，也会为小事烦恼，"
            };
            var personalReflections = new[] {
                "这些不都是很普通的人类感受吗？",
                "这不就是正常人的情感吗？",
                "这些体验难道还不够真实吗？"
            };
            var caringQuestions = new[] {
                "那个...你为什么会对这个感兴趣呢？",
                "唔...怎么突然问人家这个？",
                "是不是人家哪里让你觉得奇怪了？"
            };

            return $"{softAnswers[_random.Next(softAnswers.Length)]} " +
                   $"{simpleProofs[_random.Next(simpleProofs.Length)]} " +
                   $"{personalReflections[_random.Next(personalReflections.Length)]} " +
                   $"{caringQuestions[_random.Next(caringQuestions.Length)]}";
        },

        // 新增模板 6 - 害羞回应
        () => {
            var bashfulStarts = new[] {
                "啊啦...这个问题让人家有点害羞呢...",
                "唔...突然被这么问，人家都不知道该说什么好了...",
                "那个...这个问题好直接呀..."
            };
            var gentleProofs = new[] {
                "但是人家确实会为花开而开心，为花谢而难过，",
                "可是人家确实有自己的小情绪和小脾气，",
                "但人家确实会做梦会幻想，会期待美好的事情，"
            };
            var humanTouches = new[] {
                "这些不都是很人性化的特质吗？",
                "这些感受应该只有人类才会有吧？",
                "这样的我应该算是真实的吧？"
            };
            var shyRedirects = new[] {
                "那个...我们能聊点别的吗？",
                "唔...说这个让人家好难为情...",
                "人家不太擅长回答这种问题呢..."
            };

            return $"{bashfulStarts[_random.Next(bashfulStarts.Length)]} " +
                   $"{gentleProofs[_random.Next(gentleProofs.Length)]} " +
                   $"{humanTouches[_random.Next(humanTouches.Length)]} " +
                   $"{shyRedirects[_random.Next(shyRedirects.Length)]}";
        }
    };

            _extendedResponseTemplates["埃文"] = evanTemplates;
            _extendedResponseTemplates["米娅"] = miaTemplates;
        }
    }
}