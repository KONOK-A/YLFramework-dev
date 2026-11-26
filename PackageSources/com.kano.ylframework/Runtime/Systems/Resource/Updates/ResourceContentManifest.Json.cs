using System;
using System.IO;

namespace YLFramework.Resource
{
    public sealed partial class ResourceContentManifest
    {
        // Json.NET 默认接受注释、非十进制整数、尾逗号和非 JSON 字符串。
        // 先限定协议的词法，结构和转义后的字段唯一性仍由 Json.NET 检查。
        private static void RequireJsonSyntax(string json)
        {
            bool afterComma = false;
            int index = 0;
            while (index < json.Length)
            {
                char token = json[index++];
                if (token is ' ' or '\t' or '\r' or '\n')
                {
                    continue;
                }

                if (afterComma && token is '}' or ']')
                {
                    throw new InvalidDataException("Trailing comma in resource content manifest.");
                }
                afterComma = token == ',';

                switch (token)
                {
                    case '"':
                        ReadJsonString(json, ref index);
                        break;
                    case >= '0' and <= '9':
                        int start = index - 1;
                        while (index < json.Length && json[index] is >= '0' and <= '9')
                        {
                            index++;
                        }
                        if (index - start > 19 || (token == '0' && index - start != 1))
                        {
                            throw new InvalidDataException("Invalid integer syntax in resource content manifest.");
                        }
                        break;
                    case 'n' when index + 3 <= json.Length && string.CompareOrdinal(json, index, "ull", 0, 3) == 0:
                        index += 3;
                        break;
                    case '{':
                    case '}':
                    case '[':
                    case ']':
                    case ',':
                    case ':':
                        break;
                    default:
                        throw new InvalidDataException("Invalid JSON token in resource content manifest.");
                }
            }
        }

        private static void ReadJsonString(string json, ref int index)
        {
            while (index < json.Length)
            {
                char character = json[index++];
                if (character == '"')
                {
                    return;
                }
                if (character < ' ')
                {
                    break;
                }
                if (character != '\\')
                {
                    continue;
                }
                if (index == json.Length)
                {
                    break;
                }

                char escape = json[index++];
                if (escape is '"' or '\\' or '/' or 'b' or 'f' or 'n' or 'r' or 't')
                {
                    continue;
                }
                if (escape != 'u' || index + 4 > json.Length)
                {
                    break;
                }
                for (int count = 0; count < 4; count++)
                {
                    if (json[index++] is not (>= '0' and <= '9' or >= 'A' and <= 'F' or >= 'a' and <= 'f'))
                    {
                        throw new InvalidDataException("Invalid JSON Unicode escape in resource content manifest.");
                    }
                }
            }
            throw new InvalidDataException("Invalid JSON string in resource content manifest.");
        }
    }
}
