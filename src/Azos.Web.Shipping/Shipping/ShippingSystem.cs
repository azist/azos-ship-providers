/*<FILE_LICENSE>
 * Azos (A to Z Application Operating System) Framework
 * The A to Z Foundation (a.k.a. Azist) licenses this file to you under the MIT license.
 * See the LICENSE file in the project root for more information.
</FILE_LICENSE>*/

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

using Azos.Apps;
using Azos.Conf;
using Azos.Collections;
using Azos.Instrumentation;
using Azos.Log;

namespace Azos.Web.Shipping
{
  public abstract class ShippingSystem : DaemonWithInstrumentation<IApplicationComponent>, IWebClientCaller, IShippingSystemImplementation
  {
    #region CONSTS

      public const string USPS_CARRIER_ID        = "USPS";
      public const string DHL_EXPRESS_CARRIER_ID = "DHL_EXPRESS";
      public const string FEDEX_CARRIER_ID       = "FEDEX";
      public const string UPS_CARRIER_ID         = "UPS";

      public const string CONFIG_CARRIERS_SECTION = "carriers";
      public const string CONFIG_CARRIER_SECTION = "carrier";

      public const string CONFIG_SHIPPING_PROCESSING_SECTION = "shipping-processing";
      public const string CONFIG_SHIPPING_SYSTEM_HOST_SECTION = "shipping-system-host";
      public const string CONFIG_SHIPPING_SYSTEM_SECTION = "shipping-system";
      public const string CONFIG_AUTO_START_ATTR = "auto-start";

      private const string LOG_TOPIC = "Shipping.Processing";
      public const MessageType DEFAULT_LOG_LEVEL = MessageType.Warning;

      private static readonly TimeSpan INSTR_INTERVAL = TimeSpan.FromMilliseconds(4015);

    #endregion

    #region .ctor

      protected ShippingSystem(IApplication app) : base(app) => ctor();
      protected ShippingSystem(IApplicationComponent dir) : base(dir) => ctor();

      private void ctor()
      {
        KeepAlive = true;
        Pipelined = true;

        m_Sessions = new List<ShippingSession>();
      }

      protected override void Destructor()
      {
        DisposableObject.DisposeAndNull(ref m_InstrumentationEvent);
        base.Destructor();
      }

    #endregion

    #region Private fields

      private IConfigSectionNode m_DefaultSessionConnParamsCfg;
      private ShippingConnectionParameters m_DefaultSessionConnectParams;

      private  List<ShippingSession> m_Sessions;

      private IRegistry<ShippingCarrier> m_PreconfiguredShippingCarriers;

      private bool m_InstrumentationEnabled;
      private Time.Event m_InstrumentationEvent;
      private int m_WebServiceCallTimeoutMs;

      private long m_stat_CreateLabelCount, m_stat_CreateLabelErrorCount;
      private long m_stat_TrackShipmentCount, m_stat_TrackShipmentErrorCount;
      private long m_stat_ValidateAddressCount, m_stat_ValidateAddressErrorCount;
      private long m_stat_EstimateShippingCostErrorCount, m_stat_EstimateShippingCostCount;

    #endregion

    #region Properties

      public override string ComponentLogTopic => CoreConsts.SHIP_TOPIC;

      [Config(Default=false)]
      [ExternalParameter(CoreConsts.EXT_PARAM_GROUP_INSTRUMENTATION, CoreConsts.EXT_PARAM_GROUP_SHIPPING)]
      public override bool InstrumentationEnabled
      {
        get { return m_InstrumentationEnabled;}
        set
        {
            m_InstrumentationEnabled = value;
            if (m_InstrumentationEvent==null)
            {
              if (!value) return;
              resetStats();
              m_InstrumentationEvent = new Time.Event(App.EventTimer, null, e => AcceptManagerVisit(this, e.LocalizedTime), INSTR_INTERVAL);
            }
            else
            {
              if (value) return;
              DisposableObject.DisposeAndNull(ref m_InstrumentationEvent);
            }
        }
      }

      [Config("default-session-connect-params")]
      public IConfigSectionNode DefaultSessionConnectParamsCfg
      {
        get { return m_DefaultSessionConnParamsCfg; }
        set
        {
          m_DefaultSessionConnectParams = MakeDefaultSessionConnectParams(value);
          m_DefaultSessionConnParamsCfg = value;
        }
      }

      public IRegistry<ShippingCarrier> PreconfiguredShippingCarriers { get { return m_PreconfiguredShippingCarriers; } }

      internal List<ShippingSession> Sessions { get { return m_Sessions; } }

    #endregion

    #region IWebClientCaller

      [Config(Default = 20000)]
      public int WebServiceCallTimeoutMs
      {
        get { return m_WebServiceCallTimeoutMs; }
        set { m_WebServiceCallTimeoutMs = value < 0 ? 0 : value; }
      }

      [Config(Default = true)]
      public bool KeepAlive { get; set; }

      [Config(Default = true)]
      public bool Pipelined { get; set; }

    #endregion

    #region Public

      public abstract IShippingSystemCapabilities Capabilities { get; }

      public ShippingSession StartSession(ShippingConnectionParameters cParams = null)
      {
        return DoStartSession(cParams);
      }

      public abstract Label CreateLabel(ShippingSession session, IShippingContext context, Shipment shipment);

      public virtual TrackInfo TrackShipment(ShippingSession session, IShippingContext context, string carrierID, string trackingNumber)
      {
        return new TrackInfo
               {
                 TrackingURL = GetTrackingURL(session, context, carrierID, trackingNumber),
                 TrackingNumber = trackingNumber,
                 CarrierID = carrierID
               };
      }

      public virtual string GetTrackingURL(ShippingSession session, IShippingContext context, string carrierID, string trackingNumber)
      {
        var carrier = GetShippingCarriers(session, context).FirstOrDefault(c => c.Name.EqualsIgnoreCase(carrierID));
        if (carrier != null &&
            carrier.TrackingURL.IsNotNullOrWhiteSpace() &&
            trackingNumber.IsNotNullOrWhiteSpace())
          return carrier.TrackingURL.Args(trackingNumber);

        return null;
      }

      public abstract Address ValidateAddress(ShippingSession session, IShippingContext context, Address address, out ValidateShippingAddressException error);

      public virtual IEnumerable<ShippingCarrier> GetShippingCarriers(ShippingSession session, IShippingContext context)
      {
        return m_PreconfiguredShippingCarriers;
      }

      public abstract ShippingRate EstimateShippingCost(ShippingSession session, IShippingContext context, Shipment shipment);

    #endregion

    #region Protected

      protected ShippingConnectionParameters DefaultSessionConnectParams
      {
        get { return m_DefaultSessionConnectParams; }
      }

      protected abstract ShippingSession DoStartSession(ShippingConnectionParameters cParams = null);

      protected abstract ShippingConnectionParameters MakeDefaultSessionConnectParams(IConfigSectionNode paramsSection);

      protected override void DoConfigure(IConfigSectionNode node)
      {
        if (node == null)
        {
          node = App.ConfigRoot[CONFIG_SHIPPING_PROCESSING_SECTION];
          if (!node.Exists) return;

          //1 try to find the server with the same name as this instance
          var snode = node.Children.FirstOrDefault(cn => cn.IsSameName(CONFIG_SHIPPING_SYSTEM_SECTION) && cn.IsSameNameAttr(Name));

          //2 try to find a server without a name
          if (snode == null)
            snode = node.Children.FirstOrDefault(cn => cn.IsSameNameAttr(CONFIG_SHIPPING_SYSTEM_SECTION) && cn.AttrByName(Configuration.CONFIG_NAME_ATTR).Value.IsNullOrWhiteSpace());

          if (snode == null) return;

          node = snode;
        }

        ConfigAttribute.Apply(this, node);

        var carriers = new Registry<ShippingCarrier>();

        var snodes = node[CONFIG_CARRIERS_SECTION].Children.Where(n=>n.IsSameName(CONFIG_CARRIER_SECTION));
        foreach(var snode in snodes)
        {
            var carrier = FactoryUtils.MakeAndConfigure<ShippingCarrier>(snode, typeof(ShippingCarrier), new object[] { this });
            carriers.Register(carrier);
        }
        m_PreconfiguredShippingCarriers = carriers;
      }

      protected override void DoAcceptManagerVisit(object manager, DateTime managerNow)
      {
        dumpStats();
      }

    #endregion

    #region Stat

      protected void StatCreateLabelError()
      {
        Interlocked.Increment(ref m_stat_CreateLabelErrorCount);
      }

      protected void StatCreateLabel()
      {
        Interlocked.Increment(ref m_stat_CreateLabelCount);
      }

      protected void StatTrackShipmentErrorCount()
      {
        Interlocked.Increment(ref m_stat_TrackShipmentErrorCount);
      }

      protected void StatTrackShipmentCount()
      {
        Interlocked.Increment(ref m_stat_TrackShipmentCount);
      }

      protected void StatValidateAddressErrorCount()
      {
        Interlocked.Increment(ref m_stat_ValidateAddressErrorCount);
      }

      protected void StatValidateAddressCount()
      {
        Interlocked.Increment(ref m_stat_ValidateAddressCount);
      }

      protected void StatEstimateShippingCostErrorCount()
      {
        Interlocked.Increment(ref m_stat_EstimateShippingCostErrorCount);
      }

      protected void StatEstimateShippingCostCount()
      {
        Interlocked.Increment(ref m_stat_EstimateShippingCostCount);
      }

    #endregion

    #region .pvt

      private void dumpStats()
      {
        var src = this.Name;

        Instrumentation.LabelCount.Record(src, m_stat_CreateLabelCount);
        m_stat_CreateLabelCount = 0;

        Instrumentation.LabelErrorCount.Record(src, m_stat_CreateLabelErrorCount);
        m_stat_CreateLabelErrorCount = 0;

        Instrumentation.LabelErrorCount.Record(src, m_stat_TrackShipmentCount);
        m_stat_TrackShipmentCount = 0;

        Instrumentation.LabelErrorCount.Record(src, m_stat_TrackShipmentErrorCount);
        m_stat_TrackShipmentErrorCount = 0;

        Instrumentation.LabelErrorCount.Record(src, m_stat_ValidateAddressErrorCount);
        m_stat_ValidateAddressErrorCount = 0;

        Instrumentation.LabelErrorCount.Record(src, m_stat_ValidateAddressCount);
        m_stat_ValidateAddressCount = 0;

        Instrumentation.LabelErrorCount.Record(src, m_stat_EstimateShippingCostErrorCount);
        m_stat_EstimateShippingCostErrorCount = 0;

        Instrumentation.LabelErrorCount.Record(src, m_stat_EstimateShippingCostCount);
        m_stat_EstimateShippingCostCount = 0;
      }

      private void resetStats()
      {
        m_stat_CreateLabelCount = 0;
        m_stat_CreateLabelErrorCount = 0;
        m_stat_TrackShipmentCount = 0;
        m_stat_TrackShipmentErrorCount = 0;
        m_stat_ValidateAddressCount = 0;
        m_stat_ValidateAddressErrorCount = 0;
        m_stat_EstimateShippingCostCount = 0;
        m_stat_EstimateShippingCostErrorCount = 0;
      }

    #endregion
  }
}
