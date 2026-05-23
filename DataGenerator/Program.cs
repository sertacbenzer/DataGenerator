using Microsoft.Data.SqlClient;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Net.Http;
using System.Net.Http.Json;

namespace SyntheticDataGenerator
{
    class Program
    {
    
        private const string OllamaEndpoint = "http://localhost:11434";
        private const string LlmModel = "llama3.1:8b";           
        private const string EmbeddingModel = "bge-m3:latest";         
        private const int VectorDimension = 1024;

      
        private const string ConnectionString = "Server=localhost,1433;Database=Orion;User Id=sa;Password=Ggrt190724;TrustServerCertificate=True;";

        private const int TotalDocuments = 10100;   

        static async Task Main(string[] args)
        {
            Console.WriteLine($"🔄 {TotalDocuments} adet sentetik finansal belge üretiliyor...\n");

            using var httpClient = new HttpClient();
            httpClient.Timeout = TimeSpan.FromMilliseconds(1000000);  

            var topics = GetSampleTopics();  

            for (int i = 0; i < topics.Count; i++)
            {
                var topicInfo = topics[i % topics.Count];

                try
                {
                    // Title zaten varsa atla
                    bool titleExists = await TitleExistsAsync(topicInfo.Title);
                    if (titleExists)
                    {
                        Console.WriteLine($" {i + 1:D3} - Zaten var: {topicInfo.Title.Substring(0, Math.Min(70, topicInfo.Title.Length))}...");
                        continue;
                    }

                    var doc = await GenerateDocumentAsync(httpClient, topicInfo);

                    await InsertToSqlServerAsync(doc);

                    Console.WriteLine($"{i + 1:D3} - {doc.Title.Substring(0, Math.Min(70, doc.Title.Length))}...");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($" Hata [{i + 1}]: {ex.Message}");
                }

                // Ollama'nın aşırı yüklenmemesi için kısa bekleme
                await Task.Delay(1200);
            }

            Console.WriteLine("\n🎉 Tüm sentetik veri üretimi tamamlandı!");
        }

    
        private static async Task<FinancialDocument> GenerateDocumentAsync(
            HttpClient httpClient,
            TopicInfo topic)
        {
           string prompt = $"""
    Sen Türkiye'de ultra yüksek servet sahiplerine (1 milyon TL ve üzeri) danışmanlık veren, 
    20+ yıllık deneyimli bir Wealth Management ve Family Office uzmanısın.

    Konu: {topic.Title}

    1200 - 1800 kelime arası, **çok detaylı, profesyonel ve gerçekçi** bir rehber yaz.

    İçerikte mutlaka şunları kapsa:
    - Mevcut Türkiye ekonomik koşulları ve riskler
    - Önerilen varlık dağılımı yüzdeleri (örnek: %X altın, %Y hisse, %Z gayrimenkul)
    - Vergi, yasal yapı ve maliyet analizleri
    - Farklı senaryolar (enflasyon, resesyon, TL değer kaybı)
    - Yerel ve uluslararası fırsatlar
    - Risk yönetimi teknikleri
    - Pratik uygulama adımları ve öneriler
    - interetten günlük haber ve verileri kullanarak güncel örnekler
    - fiyat bilgilerini alırken güncel verileri kullanarak gerçekçi rakamlar ver
    - Türkiye'deki finansal ürünler, kurumlar ve hizmet sağlayıcılarla ilgili detaylı bilgiler
    - şu kadar param var nasıl değerlendirebilirim tarzında örnek vaka analizleri
    - onumuzdeki yıllarda altın, borsa, döviz, fonlar gibi varlıkların nasıl performans gösterebileceğine dair öngörüler
    - Türkiye'deki yüksek enflasyon ortamında servet koruma stratejileri
    - Türkiye'deki ekonomik ve politik gelişmelerin yatırım kararlarına etkisi
    
    Dil: Profesyonel, güven verici ve tarafsız.
    Sonunda mutlaka SPK uyarısı koy.
    """;

       
            var chatRequest = new
            {
                model = LlmModel,
                prompt = prompt,
                stream = false
            };

            var chatResponse = await httpClient.PostAsJsonAsync($"{OllamaEndpoint}/api/generate", chatRequest);
            chatResponse.EnsureSuccessStatusCode();

            var chatJsonString = await chatResponse.Content.ReadAsStringAsync();
            var chatJson = JsonDocument.Parse(chatJsonString).RootElement;
            string content = chatJson.GetProperty("response").GetString() ?? "";   

            // Embedding üret - Ollama Embedding API'ye çağrı
            var embeddingRequest = new
            {
                model = EmbeddingModel,
                prompt = content
            };

            var embeddingResponse = await httpClient.PostAsJsonAsync($"{OllamaEndpoint}/api/embeddings", embeddingRequest);
            embeddingResponse.EnsureSuccessStatusCode();

            var embeddingJsonString = await embeddingResponse.Content.ReadAsStringAsync();
            var embeddingJson = JsonDocument.Parse(embeddingJsonString).RootElement;
            var embeddingArray = embeddingJson.GetProperty("embedding").EnumerateArray()
                .Select(x => (float)x.GetDouble())
                .ToArray();

            return new FinancialDocument
            {
                Title = topic.Title,
                Content = content,
                Embedding = embeddingArray,
                Category = topic.Category,
                SubCategory = topic.SubCategory,
                RiskLevel = topic.RiskLevel,
                AssetType = topic.AssetType,
                TargetAudience = topic.TargetAudience,
                GoalType = topic.GoalType,
                Keywords = string.Join(", ", topic.Keywords),
                SourceType = "Sentetik"
            };
        }

        // ====================== BAŞLIK KONTROL ======================
        private static async Task<bool> TitleExistsAsync(string title)
        {
            using var connection = new SqlConnection(ConnectionString);
            await connection.OpenAsync();

            string sql = "SELECT COUNT(*) FROM FinancialDocuments WHERE Title = @Title";
            using var command = new SqlCommand(sql, connection);
            command.Parameters.AddWithValue("@Title", title);

            var result = await command.ExecuteScalarAsync();
            int count = result != null ? (int)result : 0;
            return count > 0;
        }

        // ====================== SQL INSERT ======================
        private static async Task InsertToSqlServerAsync(FinancialDocument doc)
        {
            using var connection = new SqlConnection(ConnectionString);
            await connection.OpenAsync();

            string sql = """
                INSERT INTO FinancialDocuments 
                (Title, Content, ContentVector, Category, SubCategory, RiskLevel, 
                 AssetType, TargetAudience, GoalType, Keywords, SourceType, CreatedDate)
                VALUES 
                (@Title, @Content, @Embedding, 
                 @Category, @SubCategory, @RiskLevel, @AssetType, 
                 @TargetAudience, @GoalType, @Keywords, @SourceType, GETDATE())
                """;

            using var command = new SqlCommand(sql, connection);

            // Embedding → JSON string for vector type
            string embeddingJson = JsonSerializer.Serialize(doc.Embedding);

            command.Parameters.AddWithValue("@Title", doc.Title);
            command.Parameters.AddWithValue("@Content", doc.Content);
            command.Parameters.AddWithValue("@Embedding", embeddingJson);
            command.Parameters.AddWithValue("@Category", doc.Category ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@SubCategory", doc.SubCategory ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@RiskLevel", doc.RiskLevel);
            command.Parameters.AddWithValue("@AssetType", doc.AssetType ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@TargetAudience", doc.TargetAudience);
            command.Parameters.AddWithValue("@GoalType", doc.GoalType ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@Keywords", doc.Keywords ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@SourceType", doc.SourceType);

            await command.ExecuteNonQueryAsync();
        }

        // ====================== ÖRNEK KONULAR ======================
       private static List<TopicInfo> GetSampleTopics()
{
  
       

    return new List<TopicInfo>
    {
        // ====================== 1. ALTIN ve KIYMETLİ MADENLER (18 Konu) ======================
        new TopicInfo { Title = "Gram Altın Nedir? Türkiye'de Gram Altın Yatırımı Detaylı Rehberi", Category = "Altın", SubCategory = "Fiziki Altın", RiskLevel = "Düşük", AssetType = "Altın", TargetAudience = "Yeni Başlayan", GoalType = "Enflasyona Karşı Koruma", Keywords = new[] { "gram altın", "fiziki altın" } },
        new TopicInfo { Title = "Yüksek Enflasyon Döneminde Gram Altın Yatırımı Stratejileri", Category = "Altın", SubCategory = "Yatırım Stratejileri", RiskLevel = "Düşük", AssetType = "Altın", TargetAudience = "Orta Seviye", GoalType = "Koruma", Keywords = new[] { "gram altın", "enflasyon" } },
        new TopicInfo { Title = "Gram Altın mı Altın Fonu mu? Detaylı Karşılaştırma ve Hangisi Daha Avantajlı?", Category = "Altın", SubCategory = "Karşılaştırma", RiskLevel = "Düşük", AssetType = "Altın", TargetAudience = "Orta Seviye", GoalType = "Karşılaştırma", Keywords = new[] { "gram altın", "altın fonu", "TEFAS" } },
        new TopicInfo { Title = "Altın Fiyatlarını Etkileyen Küresel ve Yerel Faktörler", Category = "Altın", SubCategory = "Makroekonomi", RiskLevel = "Orta", AssetType = "Altın", TargetAudience = "Orta Seviye", GoalType = "Bilgi", Keywords = new[] { "altın fiyatı" } },
        new TopicInfo { Title = "Fiziki Altın Saklama Yöntemleri ve Güvenlik Önlemleri", Category = "Altın", SubCategory = "Pratik Bilgi", RiskLevel = "Düşük", AssetType = "Altın", TargetAudience = "Tüm Seviyeler", GoalType = "Güvenlik", Keywords = new[] { "altın saklama" } },
        new TopicInfo { Title = "Cumhuriyet Altını, Gram Altın ve Çeyrek Altın Karşılaştırması", Category = "Altın", SubCategory = "Fiziki Altın", RiskLevel = "Düşük", AssetType = "Altın", TargetAudience = "Yeni Başlayan", GoalType = "Karşılaştırma", Keywords = new[] { "cumhuriyet altını" } },
        new TopicInfo { Title = "Jeopolitik Riskler ve Altın Talebi İlişkisi", Category = "Altın", SubCategory = "Makroekonomi", RiskLevel = "Orta", AssetType = "Altın", TargetAudience = "Orta Seviye", GoalType = "Bilgi", Keywords = new[] { "jeopolitik risk" } },
        new TopicInfo { Title = "Altın Alım-Satım Maliyetleri ve Spread Farkları", Category = "Altın", SubCategory = "Pratik Bilgi", RiskLevel = "Düşük", AssetType = "Altın", TargetAudience = "Orta Seviye", GoalType = "Maliyet", Keywords = new[] { "altın maliyeti" } },

        // ====================== 2. BORSA ve HİSSE SENETLERİ (20 Konu) ======================
        new TopicInfo { Title = "BIST 100 Endeksi Nedir ve Uzun Vadeli Yatırım Stratejileri", Category = "Hisse Senetleri", SubCategory = "Borsa", RiskLevel = "Yüksek", AssetType = "Hisse", TargetAudience = "Orta Seviye", GoalType = "Büyüme", Keywords = new[] { "BIST 100" } },
        new TopicInfo { Title = "Bankacılık Sektörü Hisseleri 2026 Analizi ve Risk Değerlendirmesi", Category = "Hisse Senetleri", SubCategory = "Sektör", RiskLevel = "Orta", AssetType = "Hisse", TargetAudience = "Deneyimli", GoalType = "Büyüme", Keywords = new[] { "banka hissesi" } },
        new TopicInfo { Title = "Teknoloji ve Yazılım Şirketi Hisselerinde Yatırım Fırsatları", Category = "Hisse Senetleri", SubCategory = "Sektör", RiskLevel = "Yüksek", AssetType = "Hisse", TargetAudience = "Deneyimli", GoalType = "Büyüme", Keywords = new[] { "teknoloji hissesi" } },
        new TopicInfo { Title = "Holding Şirketleri ve Çeşitlendirme Avantajı", Category = "Hisse Senetleri", SubCategory = "Sektör", RiskLevel = "Orta", AssetType = "Hisse", TargetAudience = "Orta Seviye", GoalType = "Çeşitlendirme", Keywords = new[] { "holding" } },
        new TopicInfo { Title = "BIST'te Sektör Rotasyonu Stratejileri", Category = "Hisse Senetleri", SubCategory = "Strateji", RiskLevel = "Yüksek", AssetType = "Hisse", TargetAudience = "Deneyimli", GoalType = "Strateji", Keywords = new[] { "sektör rotasyonu" } },
        new TopicInfo { Title = "Enerji Sektörü Hisseleri ve Küresel Petrol Fiyatları Etkisi", Category = "Hisse Senetleri", SubCategory = "Sektör", RiskLevel = "Yüksek", AssetType = "Hisse", TargetAudience = "Deneyimli", GoalType = "Büyüme", Keywords = new[] { "enerji sektörü" } },
        new TopicInfo { Title = "Perakende Sektörü Hisseleri ve Tüketici Güven Endeksi", Category = "Hisse Senetleri", SubCategory = "Sektör", RiskLevel = "Orta", AssetType = "Hisse", TargetAudience = "Orta Seviye", GoalType = "Büyüme", Keywords = new[] { "perakende sektörü" } },

        // ====================== 3. FONLAR ve EMEKLİLİK (12 Konu) ======================
        new TopicInfo { Title = "BES Fonları ve Devlet Katkısı ile Uzun Vadeli Tasarruf Rehberi", Category = "Fonlar", SubCategory = "Emeklilik", RiskLevel = "Düşük", AssetType = "Fon", TargetAudience = "Tüm Seviyeler", GoalType = "Uzun Vadeli", Keywords = new[] { "BES", "devlet katkısı" } },
        new TopicInfo { Title = "TEFAS Üzerinden En İyi Performans Gösteren Fonlar Nasıl Seçilir?", Category = "Fonlar", SubCategory = "Yatırım Fonları", RiskLevel = "Orta", AssetType = "Fon", TargetAudience = "Orta Seviye", GoalType = "Getiri", Keywords = new[] { "TEFAS" } },
        new TopicInfo { Title = "Altın Fonları ve Borsa Yatırım Fonları (ETF) Detaylı Rehber", Category = "Fonlar", SubCategory = "Altın", RiskLevel = "Düşük", AssetType = "Fon", TargetAudience = "Yeni Başlayan", GoalType = "Koruma", Keywords = new[] { "altın fonu", "ETF" } },

        // ====================== 4. PORTFÖY ve RİSK YÖNETİMİ (12 Konu) ======================
        new TopicInfo { Title = "Yüksek Enflasyonda Etkili Portföy Çeşitlendirme Stratejileri", Category = "Portföy Yönetimi", SubCategory = "", RiskLevel = "Orta", AssetType = "", TargetAudience = "Orta Seviye", GoalType = "Risk Yönetimi", Keywords = new[] { "portföy çeşitlendirme" } },
        new TopicInfo { Title = "Risk Profiline Göre Örnek Portföy Modelleri (Muhafazakar - Dengeli - Agresif)", Category = "Portföy Yönetimi", SubCategory = "", RiskLevel = "Orta", AssetType = "", TargetAudience = "Tüm Seviyeler", GoalType = "Kişiselleştirme", Keywords = new[] { "risk profili" } },
        new TopicInfo { Title = "Portföyde Altın, Döviz ve Hisse Oranı Nasıl Olmalı?", Category = "Portföy Yönetimi", SubCategory = "", RiskLevel = "Orta", AssetType = "", TargetAudience = "Orta Seviye", GoalType = "Koruma", Keywords = new[] { "portföy oranı" } },

        // ====================== 5. MAKROEKONOMİ, DÖVİZ ve VERGİ (15 Konu) ======================
        new TopicInfo { Title = "TCMB Faiz Politikaları ve Borsa-Döviz Üzerindeki Etkileri", Category = "Makroekonomi", SubCategory = "", RiskLevel = "Orta", AssetType = "", TargetAudience = "Orta Seviye", GoalType = "Bilgi", Keywords = new[] { "TCMB", "faiz" } },
        new TopicInfo { Title = "Dolar ve Euro Yatırımı: Riskler, Fırsatlar ve Stratejiler", Category = "Döviz", SubCategory = "", RiskLevel = "Orta", AssetType = "Döviz", TargetAudience = "Orta Seviye", GoalType = "Koruma", Keywords = new[] { "dolar yatırımı" } },
        new TopicInfo { Title = "Stopaj Vergisi ve Yatırım Maliyetlerini Azaltma Yöntemleri", Category = "Vergi", SubCategory = "", RiskLevel = "Düşük", AssetType = "", TargetAudience = "Orta Seviye", GoalType = "Optimizasyon", Keywords = new[] { "stopaj vergisi" } },
        new TopicInfo { Title = "2026 Türkiye Ekonomik Görünümü ve Yatırım Stratejileri", Category = "Makroekonomi", SubCategory = "", RiskLevel = "Orta", AssetType = "", TargetAudience = "Orta Seviye", GoalType = "Strateji", Keywords = new[] { "2026 ekonomi" } },

        // ====================== 6. DİĞER ÖNEMLİ KONULAR (18 Konu) ======================
        new TopicInfo { Title = "Davranışsal Finans ve Yatırımcıların En Sık Yaptığı 10 Hata", Category = "Davranışsal Finans", SubCategory = "", RiskLevel = "Düşük", AssetType = "", TargetAudience = "Tüm Seviyeler", GoalType = "Eğitim", Keywords = new[] { "davranışsal finans" } },
        new TopicInfo { Title = "Gayrimenkul Yatırım Fonları (GYF) ve REIT'ler", Category = "Gayrimenkul", SubCategory = "", RiskLevel = "Orta", AssetType = "GYF", TargetAudience = "Deneyimli", GoalType = "Çeşitlendirme", Keywords = new[] { "GYF" } },
        new TopicInfo { Title = "Kripto Paralar ve Türkiye'deki Yasal Regülasyon Durumu", Category = "Kripto", SubCategory = "", RiskLevel = "Çok Yüksek", AssetType = "Kripto", TargetAudience = "Deneyimli", GoalType = "Spekülasyon", Keywords = new[] { "kripto para" } },
        new TopicInfo { Title = "Kısa Vadeli Ticaret ve Teknik Analiz Temelleri", Category = "Borsa", SubCategory = "Teknik Analiz", RiskLevel = "Yüksek", AssetType = "Hisse", TargetAudience = "Deneyimli", GoalType = "Kısa Vadeli", Keywords = new[] { "teknik analiz" } },
        new TopicInfo { Title = "Merkez Bankası Rezervleri ve Türk Lirası Değeri İlişkisi", Category = "Makroekonomi", SubCategory = "", RiskLevel = "Orta", AssetType = "", TargetAudience = "Orta Seviye", GoalType = "Bilgi", Keywords = new[] { "rezerv", "TL değeri" } },
    
    // ====================== YENİ 100 KONU ======================

// ALTIN ve KIYMETLİ MADENLER (10 yeni)
new TopicInfo { Title = "Gram Altın Alırken Dikkat Edilmesi Gereken 7 Kritik Nokta", Category = "Altın", SubCategory = "Pratik Bilgi", RiskLevel = "Düşük", AssetType = "Altın", TargetAudience = "Yeni Başlayan", GoalType = "Güvenli Yatırım", Keywords = new[] { "gram altın", "alım rehberi" } },
new TopicInfo { Title = "Altın ve Gümüş Yatırımı Karşılaştırması", Category = "Altın", SubCategory = "Karşılaştırma", RiskLevel = "Orta", AssetType = "Altın", TargetAudience = "Orta Seviye", GoalType = "Çeşitlendirme", Keywords = new[] { "gümüş yatırımı" } },
new TopicInfo { Title = "Altın Sertifikası ve Banka Altın Hesabı Avantajları", Category = "Altın", SubCategory = "Dijital Altın", RiskLevel = "Düşük", AssetType = "Altın", TargetAudience = "Yeni Başlayan", GoalType = "Kolaylık", Keywords = new[] { "altın hesabı" } },

// BORSA ve HİSSE SENETLERİ (20 yeni)
new TopicInfo { Title = "Dividend (Temettü) Yatırımı Stratejileri ve Türkiye'deki Örnekler", Category = "Hisse Senetleri", SubCategory = "Temettü", RiskLevel = "Orta", AssetType = "Hisse", TargetAudience = "Orta Seviye", GoalType = "Pasif Gelir", Keywords = new[] { "temettü yatırımı" } },
new TopicInfo { Title = "BIST 30 ve BIST 100 Arasındaki Farklar ve Hangisi Daha Uygun?", Category = "Hisse Senetleri", SubCategory = "Borsa", RiskLevel = "Yüksek", AssetType = "Hisse", TargetAudience = "Orta Seviye", GoalType = "Karşılaştırma", Keywords = new[] { "BIST 30" } },
new TopicInfo { Title = "Değer Yatırımı (Value Investing) Stratejisi Türkiye'de Nasıl Uygulanır?", Category = "Hisse Senetleri", SubCategory = "Strateji", RiskLevel = "Yüksek", AssetType = "Hisse", TargetAudience = "Deneyimli", GoalType = "Uzun Vadeli", Keywords = new[] { "değer yatırımı" } },

// FONLAR ve EMEKLİLİK (12 yeni)
new TopicInfo { Title = "Fon Yönetim Ücretleri ve Gizli Maliyetler Nasıl Hesaplanır?", Category = "Fonlar", SubCategory = "Maliyet", RiskLevel = "Düşük", AssetType = "Fon", TargetAudience = "Orta Seviye", GoalType = "Maliyet Optimizasyonu", Keywords = new[] { "fon ücreti" } },
new TopicInfo { Title = "Emeklilik Fonlarında Hisse ve Altın Oranı Nasıl Olmalı?", Category = "Fonlar", SubCategory = "Emeklilik", RiskLevel = "Orta", AssetType = "Fon", TargetAudience = "Orta Seviye", GoalType = "Uzun Vadeli", Keywords = new[] { "BES fonu" } },

// PORTFÖY ve RİSK YÖNETİMİ (12 yeni)
new TopicInfo { Title = "Enflasyon + Faiz + Döviz Üçgeninde Portföy Koruma Stratejileri", Category = "Portföy Yönetimi", SubCategory = "", RiskLevel = "Orta", AssetType = "", TargetAudience = "Orta Seviye", GoalType = "Koruma", Keywords = new[] { "portföy koruma" } },
new TopicInfo { Title = "Aylık Gelir Odaklı Portföy Oluşturma Rehberi", Category = "Portföy Yönetimi", SubCategory = "", RiskLevel = "Orta", AssetType = "", TargetAudience = "Orta Seviye", GoalType = "Pasif Gelir", Keywords = new[] { "aylık gelir" } },

// MAKROEKONOMİ, DÖVİZ ve VERGİ (15 yeni)
new TopicInfo { Title = "Türk Lirası Değer Kaybı ve Yatırımcıları Nasıl Etkiliyor?", Category = "Makroekonomi", SubCategory = "", RiskLevel = "Orta", AssetType = "", TargetAudience = "Orta Seviye", GoalType = "Bilgi", Keywords = new[] { "TL değer kaybı" } },
new TopicInfo { Title = "Merkez Bankası Swap Anlaşmaları ve Ekonomik Etkileri", Category = "Makroekonomi", SubCategory = "", RiskLevel = "Orta", AssetType = "", TargetAudience = "Orta Seviye", GoalType = "Bilgi", Keywords = new[] { "swap anlaşması" } },

// DİĞER KONULAR (23 yeni)
new TopicInfo { Title = "Sürdürülebilirlik ve Yeşil Finans Ürünleri Türkiye'de", Category = "Sürdürülebilirlik", SubCategory = "", RiskLevel = "Orta", AssetType = "Fon", TargetAudience = "Deneyimli", GoalType = "Sürdürülebilirlik", Keywords = new[] { "yeşil finans" } },
new TopicInfo { Title = "Sigorta Şirketi Hisseleri ve Sektör Analizi", Category = "Hisse Senetleri", SubCategory = "Sektör", RiskLevel = "Orta", AssetType = "Hisse", TargetAudience = "Deneyimli", GoalType = "Büyüme", Keywords = new[] { "sigorta sektörü" } },
new TopicInfo { Title = "Finansal Okuryazarlık Seviyesi ve Yatırım Başarısı İlişkisi", Category = "Eğitim", SubCategory = "", RiskLevel = "Düşük", AssetType = "", TargetAudience = "Tüm Seviyeler", GoalType = "Eğitim", Keywords = new[] { "finansal okuryazarlık" } },
new TopicInfo { Title = "Yapay Zeka ve Teknoloji Trendlerinin Yatırım Dünyasına Etkisi", Category = "Teknoloji", SubCategory = "", RiskLevel = "Yüksek", AssetType = "", TargetAudience = "Deneyimli", GoalType = "Gelecek", Keywords = new[] { "yapay zeka yatırımı" } },
// ====================== YENİ 100 KONU ======================

// ALTIN ve KIYMETLİ MADENLER (10 yeni)
new TopicInfo { Title = "Gram Altın Alım Satımında Spread ve Gizli Maliyetler", Category = "Altın", SubCategory = "Pratik Bilgi", RiskLevel = "Düşük", AssetType = "Altın", TargetAudience = "Yeni Başlayan", GoalType = "Maliyet Optimizasyonu", Keywords = new[] { "gram altın", "spread maliyeti" } },
new TopicInfo { Title = "Altın ve Bitcoin Karşılaştırması: Hangisi Daha İyi Koruma Sağlar?", Category = "Altın", SubCategory = "Karşılaştırma", RiskLevel = "Orta", AssetType = "Altın", TargetAudience = "Deneyimli", GoalType = "Karşılaştırma", Keywords = new[] { "altın bitcoin" } },
new TopicInfo { Title = "Kuyumcu Altın Alım Satımında Dikkat Edilmesi Gerekenler", Category = "Altın", SubCategory = "Pratik Bilgi", RiskLevel = "Düşük", AssetType = "Altın", TargetAudience = "Yeni Başlayan", GoalType = "Güvenli Alım", Keywords = new[] { "kuyumcu altın" } },

// BORSA ve HİSSE SENETLERİ (20 yeni)
new TopicInfo { Title = "Temettü Aristokratları Stratejisi Türkiye'de Uygulanabilir mi?", Category = "Hisse Senetleri", SubCategory = "Temettü", RiskLevel = "Orta", AssetType = "Hisse", TargetAudience = "Orta Seviye", GoalType = "Pasif Gelir", Keywords = new[] { "temettü" } },
new TopicInfo { Title = "BIST'te Değer ve Büyüme Hisseleri Arasındaki Farklar", Category = "Hisse Senetleri", SubCategory = "Strateji", RiskLevel = "Yüksek", AssetType = "Hisse", TargetAudience = "Deneyimli", GoalType = "Strateji", Keywords = new[] { "değer yatırımı" } },
new TopicInfo { Title = "Küçük ve Orta Ölçekli Şirket Hisseleri (KOBİ) Yatırımı", Category = "Hisse Senetleri", SubCategory = "Sektör", RiskLevel = "Yüksek", AssetType = "Hisse", TargetAudience = "Deneyimli", GoalType = "Yüksek Getiri", Keywords = new[] { "kobi hissesi" } },

// FONLAR ve EMEKLİLİK (12 yeni)
new TopicInfo { Title = "Fonların Geçmiş Performansı Gelecek Getiriyi Garanti Eder mi?", Category = "Fonlar", SubCategory = "Performans", RiskLevel = "Düşük", AssetType = "Fon", TargetAudience = "Orta Seviye", GoalType = "Bilgi", Keywords = new[] { "fon performansı" } },
new TopicInfo { Title = "Katılma Payı Alım Satımında Dikkat Edilmesi Gerekenler", Category = "Fonlar", SubCategory = "Pratik Bilgi", RiskLevel = "Düşük", AssetType = "Fon", TargetAudience = "Yeni Başlayan", GoalType = "Pratik", Keywords = new[] { "katılma payı" } },

// PORTFÖY ve RİSK YÖNETİMİ (15 yeni)
new TopicInfo { Title = "Enflasyon, Faiz ve Döviz Şoklarına Karşı Portföy Savunma Stratejileri", Category = "Portföy Yönetimi", SubCategory = "", RiskLevel = "Orta", AssetType = "", TargetAudience = "Orta Seviye", GoalType = "Koruma", Keywords = new[] { "portföy savunma" } },
new TopicInfo { Title = "Pasif Gelir Odaklı Portföy Oluşturma Rehberi", Category = "Portföy Yönetimi", SubCategory = "", RiskLevel = "Orta", AssetType = "", TargetAudience = "Orta Seviye", GoalType = "Pasif Gelir", Keywords = new[] { "pasif gelir" } },
new TopicInfo { Title = "Portföyde Likidite Yönetimi ve Acil Durum Fonu", Category = "Portföy Yönetimi", SubCategory = "", RiskLevel = "Düşük", AssetType = "", TargetAudience = "Tüm Seviyeler", GoalType = "Risk Yönetimi", Keywords = new[] { "likidite yönetimi" } },

// MAKROEKONOMİ ve DÖVİZ (15 yeni)
new TopicInfo { Title = "Türk Lirasındaki Değer Kaybı Yatırımcıları Nasıl Etkiliyor?", Category = "Makroekonomi", SubCategory = "", RiskLevel = "Orta", AssetType = "", TargetAudience = "Orta Seviye", GoalType = "Bilgi", Keywords = new[] { "TL değer kaybı" } },
new TopicInfo { Title = "Küresel Resesyon Dönemlerinde Türkiye'de En İyi Yatırım Araçları", Category = "Makroekonomi", SubCategory = "", RiskLevel = "Yüksek", AssetType = "", TargetAudience = "Deneyimli", GoalType = "Koruma", Keywords = new[] { "resesyon" } },
new TopicInfo { Title = "Faiz Artışları ve Hisse Senetleri Arasındaki İlişki", Category = "Makroekonomi", SubCategory = "", RiskLevel = "Orta", AssetType = "", TargetAudience = "Orta Seviye", GoalType = "Bilgi", Keywords = new[] { "faiz artışı" } },

// VERGİ, SÜRDÜRÜLEBİLİRLİK ve DİĞER (23 yeni)
new TopicInfo { Title = "Yatırım Yaparken Vergi Planlaması Nasıl Yapılır?", Category = "Vergi", SubCategory = "", RiskLevel = "Düşük", AssetType = "", TargetAudience = "Orta Seviye", GoalType = "Optimizasyon", Keywords = new[] { "vergi planlaması" } },
new TopicInfo { Title = "Sürdürülebilir Yatırım Fonları ve ESG Kriterleri", Category = "Sürdürülebilirlik", SubCategory = "", RiskLevel = "Orta", AssetType = "Fon", TargetAudience = "Deneyimli", GoalType = "Sürdürülebilirlik", Keywords = new[] { "ESG", "sürdürülebilir yatırım" } },
new TopicInfo { Title = "Finansal Bağımsızlık (FIRE) Hareketi ve Türkiye'de Uygulanabilirliği", Category = "Eğitim", SubCategory = "", RiskLevel = "Orta", AssetType = "", TargetAudience = "Orta Seviye", GoalType = "Finansal Bağımsızlık", Keywords = new[] { "FIRE hareketi" } },
new TopicInfo { Title = "Yapay Zeka Çağında Finansal Danışmanlık ve Robo-Advisor'lar", Category = "Teknoloji", SubCategory = "", RiskLevel = "Orta", AssetType = "", TargetAudience = "Deneyimli", GoalType = "Gelecek", Keywords = new[] { "robo advisor" } },


new TopicInfo { Title = "Gram Altın Nedir? Türkiye'de Gram Altın Yatırımı Detaylı Rehberi", Category = "Altın", SubCategory = "Fiziki Altın", RiskLevel = "Düşük", AssetType = "Altın", TargetAudience = "Yeni Başlayan", GoalType = "Enflasyona Karşı Koruma", Keywords = new[] { "gram altın", "fiziki altın" } },
new TopicInfo { Title = "Yüksek Enflasyon Döneminde Gram Altın Yatırımı Stratejileri", Category = "Altın", SubCategory = "Yatırım Stratejileri", RiskLevel = "Düşük", AssetType = "Altın", TargetAudience = "Orta Seviye", GoalType = "Koruma", Keywords = new[] { "gram altın", "enflasyon" } },
new TopicInfo { Title = "Gram Altın mı Altın Fonu mu? Detaylı Karşılaştırma", Category = "Altın", SubCategory = "Karşılaştırma", RiskLevel = "Düşük", AssetType = "Altın", TargetAudience = "Orta Seviye", GoalType = "Karşılaştırma", Keywords = new[] { "gram altın", "altın fonu" } },
new TopicInfo { Title = "Altın Fiyatlarını Etkileyen Küresel ve Yerel Faktörler", Category = "Altın", SubCategory = "Makroekonomi", RiskLevel = "Orta", AssetType = "Altın", TargetAudience = "Orta Seviye", GoalType = "Bilgi", Keywords = new[] { "altın fiyatı" } },
new TopicInfo { Title = "Fiziki Altın Saklama Yöntemleri ve Güvenlik Önlemleri", Category = "Altın", SubCategory = "Pratik Bilgi", RiskLevel = "Düşük", AssetType = "Altın", TargetAudience = "Tüm Seviyeler", GoalType = "Güvenlik", Keywords = new[] { "altın saklama" } },
new TopicInfo { Title = "Cumhuriyet Altını ve Gram Altın Karşılaştırması", Category = "Altın", SubCategory = "Fiziki Altın", RiskLevel = "Düşük", AssetType = "Altın", TargetAudience = "Yeni Başlayan", GoalType = "Karşılaştırma", Keywords = new[] { "cumhuriyet altını" } },
new TopicInfo { Title = "Jeopolitik Risklerde Altın Talebi ve Fiyat Dinamikleri", Category = "Altın", SubCategory = "Makroekonomi", RiskLevel = "Orta", AssetType = "Altın", TargetAudience = "Orta Seviye", GoalType = "Bilgi", Keywords = new[] { "jeopolitik risk" } },

new TopicInfo { Title = "BIST 100 Endeksi ve Uzun Vadeli Yatırım Stratejileri", Category = "Hisse Senetleri", SubCategory = "Borsa", RiskLevel = "Yüksek", AssetType = "Hisse", TargetAudience = "Orta Seviye", GoalType = "Büyüme", Keywords = new[] { "BIST 100" } },
new TopicInfo { Title = "Bankacılık Sektörü Hisseleri 2026 Analizi", Category = "Hisse Senetleri", SubCategory = "Sektör", RiskLevel = "Orta", AssetType = "Hisse", TargetAudience = "Deneyimli", GoalType = "Büyüme", Keywords = new[] { "banka hissesi" } },
new TopicInfo { Title = "Teknoloji ve Yazılım Şirketi Hisselerinde Yatırım Fırsatları", Category = "Hisse Senetleri", SubCategory = "Sektör", RiskLevel = "Yüksek", AssetType = "Hisse", TargetAudience = "Deneyimli", GoalType = "Büyüme", Keywords = new[] { "teknoloji hissesi" } },
new TopicInfo { Title = "Temettü Odaklı Yatırım Stratejileri", Category = "Hisse Senetleri", SubCategory = "Temettü", RiskLevel = "Orta", AssetType = "Hisse", TargetAudience = "Orta Seviye", GoalType = "Pasif Gelir", Keywords = new[] { "temettü" } },
new TopicInfo { Title = "Holding Şirketleri ve Çeşitlendirme Avantajı", Category = "Hisse Senetleri", SubCategory = "Sektör", RiskLevel = "Orta", AssetType = "Hisse", TargetAudience = "Orta Seviye", GoalType = "Çeşitlendirme", Keywords = new[] { "holding" } },

new TopicInfo { Title = "BES Fonları ve Devlet Katkısı ile Uzun Vadeli Tasarruf Rehberi", Category = "Fonlar", SubCategory = "Emeklilik", RiskLevel = "Düşük", AssetType = "Fon", TargetAudience = "Tüm Seviyeler", GoalType = "Uzun Vadeli", Keywords = new[] { "BES" } },
new TopicInfo { Title = "TEFAS Üzerinden En İyi Fon Seçimi Kriterleri", Category = "Fonlar", SubCategory = "Yatırım Fonları", RiskLevel = "Orta", AssetType = "Fon", TargetAudience = "Orta Seviye", GoalType = "Getiri", Keywords = new[] { "TEFAS" } },
new TopicInfo { Title = "Altın Fonları ve Borsa Yatırım Fonları (ETF) Rehberi", Category = "Fonlar", SubCategory = "Altın", RiskLevel = "Düşük", AssetType = "Fon", TargetAudience = "Yeni Başlayan", GoalType = "Koruma", Keywords = new[] { "altın fonu", "ETF" } },

new TopicInfo { Title = "1 Milyar TL Servet için Optimal Portföy Dağılımı", Category = "Büyük Servet Yönetimi", SubCategory = "Portföy", RiskLevel = "Orta", AssetType = "", TargetAudience = "Ultra Yüksek Servet", GoalType = "Servet Koruma", Keywords = new[] { "1 milyar tl", "portföy dağılımı" } },
new TopicInfo { Title = "Yüksek Enflasyonda Portföy Çeşitlendirme Stratejileri", Category = "Portföy Yönetimi", SubCategory = "", RiskLevel = "Orta", AssetType = "", TargetAudience = "Orta Seviye", GoalType = "Risk Yönetimi", Keywords = new[] { "portföy çeşitlendirme" } },
new TopicInfo { Title = "Risk Profiline Göre Portföy Modelleri", Category = "Portföy Yönetimi", SubCategory = "", RiskLevel = "Orta", AssetType = "", TargetAudience = "Tüm Seviyeler", GoalType = "Kişiselleştirme", Keywords = new[] { "risk profili" } },

new TopicInfo { Title = "TCMB Faiz Politikaları ve Borsa-Döviz Etkileri", Category = "Makroekonomi", SubCategory = "Para Politikası", RiskLevel = "Orta", AssetType = "", TargetAudience = "Orta Seviye", GoalType = "Bilgi", Keywords = new[] { "TCMB", "faiz" } },
new TopicInfo { Title = "Dolar ve Euro Yatırımı Riskleri ve Fırsatları", Category = "Döviz", SubCategory = "", RiskLevel = "Orta", AssetType = "Döviz", TargetAudience = "Orta Seviye", GoalType = "Koruma", Keywords = new[] { "dolar yatırımı" } },
new TopicInfo { Title = "Stopaj Vergisi ve Yatırım Maliyetlerini Azaltma Yöntemleri", Category = "Vergi", SubCategory = "", RiskLevel = "Düşük", AssetType = "", TargetAudience = "Orta Seviye", GoalType = "Optimizasyon", Keywords = new[] { "stopaj vergisi" } },
new TopicInfo { Title = "2026 Türkiye Ekonomik Görünümü ve Yatırım Stratejileri", Category = "Makroekonomi", SubCategory = "", RiskLevel = "Orta", AssetType = "", TargetAudience = "Orta Seviye", GoalType = "Strateji", Keywords = new[] { "2026 ekonomi" } },
new TopicInfo { Title = "Davranışsal Finans ve Yatırımcıların En Sık Yaptığı Hatalar", Category = "Davranışsal Finans", SubCategory = "", RiskLevel = "Düşük", AssetType = "", TargetAudience = "Tüm Seviyeler", GoalType = "Eğitim", Keywords = new[] { "davranışsal finans" } },
new TopicInfo { Title = "Gayrimenkul Yatırım Fonları (GYF) ve REIT'ler", Category = "Gayrimenkul", SubCategory = "", RiskLevel = "Orta", AssetType = "GYF", TargetAudience = "Deneyimli", GoalType = "Çeşitlendirme", Keywords = new[] { "GYF" } },
new TopicInfo { Title = "Kripto Paralar ve Türkiye'deki Yasal Regülasyon Durumu", Category = "Kripto", SubCategory = "", RiskLevel = "Çok Yüksek", AssetType = "Kripto", TargetAudience = "Deneyimli", GoalType = "Spekülasyon", Keywords = new[] { "kripto para" } },
new TopicInfo { Title = "Aile Ofisi Yapısı ve Türkiye'de Uygulanması", Category = "Büyük Servet Yönetimi", SubCategory = "Aile Ofisi", RiskLevel = "Düşük", AssetType = "", TargetAudience = "Ultra Yüksek Servet", GoalType = "Profesyonel Yönetim", Keywords = new[] { "aile ofisi" } },
new TopicInfo { Title = "Yurtdışı Yatırım ve Servet Diversifikasyonu", Category = "Uluslararası", SubCategory = "", RiskLevel = "Orta", AssetType = "", TargetAudience = "Ultra Yüksek Servet", GoalType = "Diversifikasyon", Keywords = new[] { "yurtdışı yatırım" } }
    

    





    };
}
    
    
    
    };

    

    

    // ====================== MODEL SINIFLARI ======================
    public class FinancialDocument
    {
        public string Title { get; set; } = string.Empty;
        public string Content { get; set; } = string.Empty;
        public float[] Embedding { get; set; } = Array.Empty<float>();
        public string Category { get; set; } = string.Empty;
        public string? SubCategory { get; set; }
        public string RiskLevel { get; set; } = string.Empty;
        public string? AssetType { get; set; }
        public string TargetAudience { get; set; } = string.Empty;
        public string? GoalType { get; set; }
        public string? Keywords { get; set; }
        public string SourceType { get; set; } = "Sentetik";
    }

    public class TopicInfo
    {
        public string Title { get; set; } = string.Empty;
        public string Topic { get; set; } = string.Empty;
        public string Category { get; set; } = string.Empty;
        public string SubCategory { get; set; } = string.Empty;
        public string RiskLevel { get; set; } = string.Empty;
        public string AssetType { get; set; } = string.Empty;
        public string TargetAudience { get; set; } = string.Empty;
        public string GoalType { get; set; } = string.Empty;
        public string[] Keywords { get; set; } = Array.Empty<string>();
    }

}

