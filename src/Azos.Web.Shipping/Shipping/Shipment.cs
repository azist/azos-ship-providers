/*<FILE_LICENSE>
 * Azos (A to Z Application Operating System) Framework
 * The A to Z Foundation (a.k.a. Azist) licenses this file to you under the MIT license.
 * See the LICENSE file in the project root for more information.
</FILE_LICENSE>*/

using System;
using System.Collections.Generic;
using System.Linq;

using Azos.Conf;
using Azos.Collections;
using Azos.Serialization.JSON;
using Azos.Standards;

namespace Azos.Web.Shipping
{
  /// <summary>
  /// Represents shipping carrier within the scope of some ShippingSystem
  /// </summary>
  public class ShippingCarrier : IConfigurable, INamed
  {
    #region Inner classes

      /// <summary>
      /// Represents shipping service i.e. USPS First Class, FedEx Ground etc.
      /// </summary>
      public class Service : IConfigurable, INamed
      {
        public Service(ShippingCarrier carrier)
        {
          if (carrier==null) throw new ShippingException("Service.ctor(carrier=null)");

          m_Carrier = carrier;
        }

        private readonly ShippingCarrier m_Carrier;

        public ShippingCarrier Carrier { get { return m_Carrier; } }

        [Config] public string Name { get; set; }
        [Config] public NLSMap NLSName { get; set; }

        [Config] public PriceCategory PriceCategory { get; set; }
        [Config] public bool IncludeBusinessDays { get; set; }
        [Config] public bool IncludeSaturdays { get; set; }
        [Config] public bool IncludeSundays { get; set; }
        [Config] public bool Trackable { get; set; }
        [Config] public bool Insurance { get; set; }

        public void Configure(IConfigSectionNode node)
      {
        ConfigAttribute.Apply(this, node);

        NLSName = new NLSMap(node[CONFIG_NLS_SECTION]);
      }
      }

      /// <summary>
      /// Represents shipping package supplied with type (i.e. Envelope, Box etc.)
      /// </summary>
      public class Package : IConfigurable, INamed
      {
        public Package(ShippingCarrier carrier)
      {
        if (carrier==null) throw new ShippingException("Template.ctor(carrier=null)");

        m_Carrier = carrier;
      }

        private readonly ShippingCarrier m_Carrier;

        public ShippingCarrier Carrier { get { return m_Carrier; } }

        [Config] public string Name { get; set; }
        [Config] public NLSMap NLSName { get; set; }
        [Config("package-type")] PackageType Type { get; set; }

        [Config] public Distance.UnitType DistanceUnit { get; set; }
        [Config] public decimal Length { get; set; }
        [Config] public decimal Width  { get; set; }
        [Config] public decimal Height { get; set; }
        [Config] public Weight.UnitType WeightUnit { get; set; }
        [Config] public decimal Weight { get; set; }

        public void Configure(IConfigSectionNode node)
      {
        ConfigAttribute.Apply(this, node);

        NLSName = new NLSMap(node[CONFIG_NLS_SECTION]);
      }
      }

    #endregion

    #region CONST

      public const string CONFIG_SERVICES_SECTION = "services";
      public const string CONFIG_SERVICE_SECTION  = "service";
      public const string CONFIG_PACKAGES_SECTION = "packages";
      public const string CONFIG_PACKAGE_SECTION  = "package";
      public const string CONFIG_NLS_SECTION      = "nls-name";

    #endregion

    public ShippingCarrier(ShippingSystem shippingSystem)
    {
      if (shippingSystem==null) throw new ShippingException("Carrier.ctor(shippingSystem=null)");

      m_ShippingSystem = shippingSystem;
      m_Services = new Registry<Service>();
      m_Packages = new Registry<Package>();
    }

    private readonly ShippingSystem m_ShippingSystem;
    private readonly Registry<Service> m_Services;
    private readonly Registry<Package> m_Packages;

    public ShippingSystem ShippingSystem { get { return m_ShippingSystem; } }

    [Config("$carrier-type")] public CarrierType Type { get; set; }
    [Config] public string Name { get; set; }
    [Config] public NLSMap NLSName { get; set; }
    [Config] public string TrackingURL { get; set; }

    public byte[] Logo { get; set; }

    public IRegistry<Service> Services { get { return m_Services; } }
    public IRegistry<Package> Packages { get { return m_Packages; } }


    public void Configure(IConfigSectionNode node)
    {
      ConfigAttribute.Apply(this, node);

      var mnodes = node[CONFIG_SERVICES_SECTION].Children.Where(n=>n.IsSameName(CONFIG_SERVICE_SECTION));
      foreach(var mnode in mnodes)
      {
          var service = FactoryUtils.MakeAndConfigure<Service>(mnode, typeof(Service), new object[] { this });
          m_Services.Register(service);
      }

      var templates = new List<Package>();
      var tnodes = node[CONFIG_PACKAGES_SECTION].Children.Where(n=>n.IsSameName(CONFIG_PACKAGE_SECTION));
      foreach(var tnode in tnodes)
      {
          var template = FactoryUtils.MakeAndConfigure<Package>(tnode, typeof(Package), new object[] { this });
          m_Packages.Register(template);
      }

      NLSName = new NLSMap(node[CONFIG_NLS_SECTION]);
    }
  }

  /// <summary>
  /// Represents shipping rate data
  /// </summary>
  public class ShippingRate
  {
    public string CarrierID { get; set; }
    public string ServiceID { get; set; }
    public string PackageID { get; set; }

    /// <summary>
    /// If true indicates that there is no available rate was found for initial shipping data,
    /// so shipping system changed carrier/service to most appropriate one
    /// </summary>
    public bool IsAlternative { get; set; }

    public Financial.Amount? Cost { get; set; }
  }

  /// <summary>
  /// Represents abstraction of shipment - a package (i.e. Envelope) along with its shipping service (i.e. USPS Ground)
  /// </summary>
  public class Shipment
  {
    public ShippingCarrier Carrier { get; set; }
    public ShippingCarrier.Service Service { get; set; }
    public ShippingCarrier.Package Package { get; set; }

    public Distance.UnitType DistanceUnit { get; set; }
    public decimal Length { get; set; }
    public decimal Width { get; set; }
    public decimal Height { get; set; }

    public Weight.UnitType WeightUnit { get; set; }
    public decimal Weight { get; set; }

    public LabelFormat LabelFormat { get; set; }
    public string LabelIDForReturn { get; set; }

    public Address FromAddress { get; set; }
    public Address ToAddress { get; set; }
    public Address ReturnAddress { get; set; }
  }

  /// <summary>
  /// Tracking data for shipping.
  /// Contains information about history tracking locations, tracking status etc.
  /// </summary>
  public class TrackInfo
  {
    #region Inner

      /// <summary>
      /// Tracking history item. Contains information about history tracking locations, tracking status etc.
      /// </summary>
      public class HistoryItem
      {
        public DateTime?   Date            { get; set; }
        public TrackStatus Status          { get; set; }
        public string      Details         { get; set; }
        public Address     CurrentLocation { get; set; }
      }

    #endregion

    public TrackInfo()
    {
      History = new List<HistoryItem>();
    }

    public string      CarrierID       { get; set; }
    public string      ServiceID       { get; set; }
    public string      TrackingURL     { get; set; }
    public string      TrackingNumber  { get; set; }
    public DateTime?   Date            { get; set; }
    public TrackStatus Status          { get; set; }
    public string      Details         { get; set; }
    public Address     FromAddress     { get; set; }
    public Address     ToAddress       { get; set; }
    public Address     CurrentLocation { get; set; }

    public List<HistoryItem> History { get; private set; }
  }

}
