using System.Net;
using System.Text;
using ImmichFrame.WebApi.Helpers;
using NUnit.Framework;

namespace ImmichFrame.WebApi.Tests.Helpers;

[TestFixture]
public class OpenNextionMdnsTests
{
    private static byte[] Query(string name, int type)
    {
        var bytes = new List<byte> { 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0 };
        foreach (var label in name.Split('.'))
        {
            bytes.Add((byte)label.Length);
            bytes.AddRange(Encoding.ASCII.GetBytes(label));
        }
        bytes.AddRange(new byte[] { 0, (byte)(type >> 8), (byte)type, 0, 1 });
        return bytes.ToArray();
    }

    [Test]
    public void AnswersOnlyQueriesForTheService()
    {
        Assert.Multiple(() =>
        {
            Assert.That(OpenNextionMdns.AsksForService(Query("_immichframe._tcp.local", 12)), Is.True);
            Assert.That(OpenNextionMdns.AsksForService(Query("_home-assistant._tcp.local", 12)), Is.False);
            Assert.That(OpenNextionMdns.AsksForService(new byte[3]), Is.False);
        });
    }

    [Test]
    public void ResponseCarriesTheServiceRecordsAndAddress()
    {
        var packet = OpenNextionMdns.BuildResponse("Frame", "box", 8080, [IPAddress.Parse("192.168.1.20")], null, "1.0");
        var text = Encoding.ASCII.GetString(packet);
        Assert.Multiple(() =>
        {
            Assert.That(packet[2] & 0x80, Is.Not.Zero, "a response");
            Assert.That(packet[7], Is.EqualTo(4), "PTR, SRV, TXT and one A record");
            Assert.That(text, Does.Contain("url=http://192.168.1.20:8080"));
            Assert.That(text, Does.Contain("_immichframe"));
        });
    }
}
