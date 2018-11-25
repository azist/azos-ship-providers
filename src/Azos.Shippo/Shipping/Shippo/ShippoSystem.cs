/*<FILE_LICENSE>
 * Azos (A to Z Application Operating System) Framework
 * The A to Z Foundation (a.k.a. Azist) licenses this file to you under the MIT license.
 * See the LICENSE file in the project root for more information.
</FILE_LICENSE>*/

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

using Azos.Conf;
using Azos.Data;
using Azos.Log;
using Azos.Serialization.JSON;
using Azos.Standards;

namespace Azos.Web.Shipping.Shippo
{
  public class ShippoSystem : ShippingSystem
  {
    #region CONSTS

      public const string SHIPPO_REALM = "shippo";

      public const string PURCHASE_PURPOSE = "PURCHASE";
      public const string QUOTE_PURPOSE    = "QUOTE";
      public const string STATUS_SUCCESS   = "SUCCESS";
      public const string STATUS_ERROR     = "ERROR";
      public const string STATUS_VALID     = "VALID";
      public const string STATUS_INVALID   = "INVALID";
      public const string CODE_INVALID     = "Invalid";

      public const string HDR_AUTHORIZATION       = "Authorization";
      public const string HDR_AUTHORIZATION_TOKEN = "ShippoToken {0}";

      public const string URI_TRACKING_FORM   = "http://tracking.goshippo.com/";
      public const string URI_API_BASE        = "https://api.goshippo.com";
      public const string URI_TRACKING_BY_NUM = URI_TRACKING_FORM+"{0}/{1}";
      public const string URI_TRANSACTIONS    = "/v1/transactions";
      public const string URI_RATES           = "/v1/rates/{0}";
      public const string URI_TRACKING        = "/v1/tracks/{0}/{1}";
      public const string URI_ADDRESS         = "/v1/addresses";
      public const string URI_SHIPMENTS       = "/v1/shipments";

      public static readonly Dictionary<LabelFormat, string> FORMATS = new Dictionary<LabelFormat, string>
      {
        { LabelFormat.PDF, "PDF" },
        { LabelFormat.PDF_4X6 , "PDF_4X6" },
        { LabelFormat.PNG, "PNG" },
        { LabelFormat.ZPLII, "ZPLII" }
      };

      public static readonly Dictionary<Distance.UnitType, string> DIST_UNITS = new Dictionary<Distance.UnitType, string>
      {
        { Distance.UnitType.Cm, "cm" },
        { Distance.UnitType.In, "in" },
        { Distance.UnitType.Ft, "ft" },
        { Distance.UnitType.Mm, "mm" },
        { Distance.UnitType.M,  "m"  },
        { Distance.UnitType.Yd, "yd" }
      };

      public static readonly Dictionary<Weight.UnitType, string> WEIGHT_UNITS = new Dictionary<Weight.UnitType, string>
      {
        { Weight.UnitType.G,  "g"  },
        { Weight.UnitType.Oz, "oz" },
        { Weight.UnitType.Lb, "lb" },
        { Weight.UnitType.Kg, "kg" }
      };

      public static readonly Dictionary<CarrierType, string> CARRIERS = new Dictionary<CarrierType, string>
      {
        { CarrierType.USPS,       "usps" },
        { CarrierType.DHLExpress, "dhl_express" },
        { CarrierType.FedEx,      "fedex" },
        { CarrierType.UPS,        "ups" }
      };

      public static readonly Dictionary<string, TrackStatus> TRACK_STATUSES = new Dictionary<string, TrackStatus>
      {
        { "UNKNOWN",   TrackStatus.Unknown },
        { "DELIVERED", TrackStatus.Delivered },
        { "TRANSIT",   TrackStatus.Transit },
        { "FAILURE",   TrackStatus.Failure },
        { "RETURNED",  TrackStatus.Returned }
      };

    #endregion

    #region .ctor

      public ShippoSystem(string name, IConfigSectionNode node) : base(name, node)
      {
      }

      public ShippoSystem(string name, IConfigSectionNode node, object director) : base(name, node, director)
      {
      }

    #endregion

    #region IShippingSystem impl

      public override IShippingSystemCapabilities Capabilities
      {
        get { return ShippoCapabilities.Instance; }
      }

      protected override ShippingSession DoStartSession(ShippingConnectionParameters cParams = null)
      {
        cParams = cParams ?? DefaultSessionConnectParams;
        return new ShippoSession(this, (ShippoConnectionParameters)cParams);
      }

      protected override ShippingConnectionParameters MakeDefaultSessionConnectParams(IConfigSectionNode paramsSection)
      {
        return ShippingConnectionParameters.Make<ShippoConnectionParameters>(paramsSection);
      }

      public override Label CreateLabel(ShippingSession session, IShippingContext context, Shipment shipment)
      {
        return CreateLabel((ShippoSession)session, context, shipment);
      }

      public Label CreateLabel(ShippoSession session, IShippingContext context, Shipment shipment)
      {
        var logID = WriteLog(MessageType.Info, "CreateLabel()", StringConsts.SHIPPO_CREATE_LABEL_MESSAGE.Args(shipment.FromAddress, shipment.ToAddress));

        try
        {
          return doCreateLabel(session, context, shipment, logID);
        }
        catch (Exception ex)
        {
          StatCreateLabelError();

          var header = StringConsts.SHIPPO_CREATE_LABEL_ERROR.Args(shipment.FromAddress, shipment.ToAddress, ex.ToMessageWithType());
          WriteLog(MessageType.Error, "CreateLabel()", header, ex, logID);
          var error = ShippingException.ComposeError(ex.Message, ex);

          throw error;
        }
      }

      public override TrackInfo TrackShipment(ShippingSession session, IShippingContext context, string carrierID, string trackingNumber)
      {
        // spol 20170412: redirect to carrier's tracking page for now; use the method below to enable Shippo tracking
        return base.TrackShipment(session, context, carrierID, trackingNumber);
      }

      public TrackInfo TrackShipment(ShippoSession session, IShippingContext context, string carrierID, string trackingNumber)
      {
        var logID = WriteLog(MessageType.Info, "TrackShipment()", StringConsts.SHIPPO_TRACK_SHIPMENT_MESSAGE.Args(trackingNumber));

        try
        {
          var result = doTrackShipment(session, context, carrierID, trackingNumber, logID);
          result.TrackingNumber = trackingNumber;
          result.TrackingURL = GetTrackingURL(session, context, carrierID, trackingNumber);
          result.CarrierID = carrierID;

          return result;
        }
        catch (Exception ex)
        {
          StatTrackShipmentErrorCount();

          var header = StringConsts.SHIPPO_TRACK_SHIPMENT_ERROR.Args(trackingNumber,ex.ToMessageWithType());
          WriteLog(MessageType.Error, "TrackShipment()", header, ex, logID);
          var error = ShippingException.ComposeError(ex.Message, ex);

          throw error;
        }
      }

      public override string GetTrackingURL(ShippingSession session, IShippingContext context, string carrierID, string trackingNumber)
      {
        return GetTrackingURL((ShippoSession)session, context, carrierID, trackingNumber);
      }

      public string GetTrackingURL(ShippoSession session, IShippingContext context, string carrierID, string trackingNumber)
      {
        var url = base.GetTrackingURL(session, context, carrierID, trackingNumber);

        if (url.IsNullOrWhiteSpace() &&
            carrierID.IsNotNullOrWhiteSpace() &&
            trackingNumber.IsNotNullOrWhiteSpace())
        {
          var carrier = GetShippingCarriers(session, context).FirstOrDefault(c => c.Name.EqualsIgnoreCase(carrierID));
          if (carrier==null) return null;

          string ccode;
          if (!CARRIERS.TryGetValue(carrier.Type, out ccode)) return null;

          return URI_TRACKING_BY_NUM.Args(carrierID, trackingNumber);
        }

        return url;
      }

      public override Address ValidateAddress(ShippingSession session, IShippingContext context, Address address, out ValidateShippingAddressException error)
      {
        return ValidateAddress((ShippoSession)session, context, address, out error);
      }

      public Address ValidateAddress(ShippoSession session, IShippingContext context, Address address, out ValidateShippingAddressException error)
      {
        var logID = WriteLog(MessageType.Info, "ValidateAddress()", StringConsts.SHIPPO_VALIDATE_ADDRESS_MESSAGE);

        try
        {
          return doValidateAddress(session, context, address, logID, out error);
        }
        catch (Exception ex)
        {
          StatValidateAddressErrorCount();

          var header = StringConsts.SHIPPO_VALIDATE_ADDRESS_ERROR.Args(ex.ToMessageWithType());
          WriteLog(MessageType.Error, "ValidateAddress()", header, ex, logID);
          throw ShippingException.ComposeError(ex.Message, ex);
        }
      }

      public override ShippingRate EstimateShippingCost(ShippingSession session, IShippingContext context, Shipment shipment)
      {
        return EstimateShippingCost((ShippoSession)session, context, shipment);
      }

      public ShippingRate EstimateShippingCost(ShippoSession session, IShippingContext context, Shipment shipment)
      {
        var logID = WriteLog(MessageType.Info, "EstimateShippingCost()", StringConsts.SHIPPO_ESTIMATE_SHIPPING_COST_MESSAGE.Args(shipment.FromAddress, shipment.ToAddress, shipment.Service.Name));

        try
        {
          return doEstimateShippingCost(session, context, shipment, logID);
        }
        catch (Exception ex)
        {
          StatEstimateShippingCostErrorCount();

          var header = StringConsts.SHIPPO_ESTIMATE_SHIPPING_COST_ERROR.Args(shipment.FromAddress, shipment.ToAddress, shipment.Service.Name, ex.ToMessageWithType());
          WriteLog(MessageType.Error, "EstimateShippingCost()", header, ex, logID);
          var error = ShippingException.ComposeError(ex.Message, ex);

          throw error;
        }
      }

    #endregion

    #region .pvt

      private Label doCreateLabel(ShippoSession session, IShippingContext context, Shipment shipment, Guid logID)
      {
        var cred = (ShippoCredentials)session.User.Credentials;

        // label request
        var request = new WebClient.RequestParams(this)
        {
          Method = HTTPRequestMethod.POST,
          ContentType = ContentType.JSON,
          Headers = new Dictionary<string, string>
            {
              { HDR_AUTHORIZATION, HDR_AUTHORIZATION_TOKEN.Args(cred.PrivateToken) }
            },
          Body = getCreateLabelRequestBody(session, shipment).ToJSON(JSONWritingOptions.Compact)
        };

        var response = WebClient.GetJson(new Uri(URI_API_BASE + URI_TRANSACTIONS), request);

        WriteLog(MessageType.Info, "doCreateLabel()", response.ToJSON(), relatedMessageID: logID);

        checkResponse(response);

        // get label bin data from URL in response
        string labelURL = response["label_url"].AsString().EscapeURIStringWithPlus();

        // get used rate, fill label's data
        var id = response["object_id"].AsString();
        var trackingNumber = response["tracking_number"].AsString();
        var rate = doGetRate(session, response["rate"].AsString(), logID);
        var amount = rate != null ?
                     new Azos.Financial.Amount(rate["currency"].AsString(), rate["amount"].AsDecimal()) :
                     new Azos.Financial.Amount(string.Empty, 0);

        var label = new Label(id,
                              labelURL,
                              shipment.LabelFormat,
                              trackingNumber,
                              shipment.Carrier.Type,
                              amount);

        StatCreateLabel();

        return label;
      }

      private TrackInfo doTrackShipment(ShippoSession session, IShippingContext context, string carrierID, string trackingNumber, Guid logID)
      {
        if (trackingNumber.IsNullOrWhiteSpace())
          throw new ShippingException("Tracking number is empty");

        var carrier = GetShippingCarriers(session, context).FirstOrDefault(c => c.Name.EqualsIgnoreCase(carrierID));
        if (carrier==null)
          throw new ShippingException("Unknown carrier");

        string ccode;
        if (!CARRIERS.TryGetValue(carrier.Type, out ccode))
          throw new ShippingException("Unknown carrier");

        var cred = (ShippoCredentials)session.User.Credentials;

        var request = new WebClient.RequestParams(this)
        {
          Method = HTTPRequestMethod.GET,
          ContentType = ContentType.JSON,
          Headers = new Dictionary<string, string>
            {
              { HDR_AUTHORIZATION, HDR_AUTHORIZATION_TOKEN.Args(cred.PrivateToken) }
            }
        };

        var response = WebClient.GetJson(new Uri((URI_API_BASE + URI_TRACKING).Args(ccode, trackingNumber)), request);

        var result = new TrackInfo();

        var status = response["tracking_status"] as JSONDataMap;
        if (status == null)
          throw new ShippingException("Tracking status is not available");

        TrackStatus ts;
        if (status["status"] != null && TRACK_STATUSES.TryGetValue(status["status"].AsString(), out ts))
          result.Status = ts;

        DateTime date;
        if (status["status_date"] != null && DateTime.TryParse(status["status_date"].AsString(), out date))
          result.Date = date;

        result.Details = status["status_details"].AsString();
        result.CurrentLocation = getAddressFromJSON(status["location"] as JSONDataMap);

        var service = response["servicelevel"] as JSONDataMap;
        if (service != null) result.ServiceID = service["name"].AsString();
        result.FromAddress = getAddressFromJSON(response["address_from"] as JSONDataMap);
        result.ToAddress = getAddressFromJSON(response["address_to"] as JSONDataMap);

        var history = response["tracking_history"] as JSONDataArray;
        if (history != null)
        {
          foreach (JSONDataMap hitem in history)
          {
            var hi = new TrackInfo.HistoryItem();

            if (hitem["status"] != null && TRACK_STATUSES.TryGetValue(hitem["status"].AsString(), out ts))
              hi.Status = ts;

            hi.Details = hitem["status_details"].AsString();

            if (hitem["status_date"] != null && DateTime.TryParse(hitem["status_date"].AsString(), out date))
              hi.Date = date;

            hi.CurrentLocation = getAddressFromJSON(hitem["location"] as JSONDataMap);

            result.History.Add(hi);
          }
        }

        return result;
      }

      private Address doValidateAddress(ShippoSession session, IShippingContext context, Address address, Guid logID, out ValidateShippingAddressException error)
      {
        error = null;
        var cred = (ShippoCredentials)session.User.Credentials;
        var body = getAddressBody(address);
        body["validate"] = true;

        // validate address request
        var request = new WebClient.RequestParams(this)
        {
          Method = HTTPRequestMethod.POST,
          ContentType = ContentType.JSON,
          Headers = new Dictionary<string, string>
            {
              { HDR_AUTHORIZATION, HDR_AUTHORIZATION_TOKEN.Args(cred.PrivateToken) }
            },
          Body = body.ToJSON(JSONWritingOptions.Compact)
        };

        var response = WebClient.GetJson(new Uri(URI_API_BASE + URI_ADDRESS), request);

        WriteLog(MessageType.Info, "doValidateAddress()", response.ToJSON(), relatedMessageID: logID);

        // check for validation errors:
        // Shippo API can return STATUS_INVALID or (!!!) STATUS_VALID but with 'code'="Invalid"
        var state = response["object_state"].AsString(STATUS_INVALID);
        var messages = response["messages"] as JSONDataArray;
        JSONDataMap message = null;
        var code = string.Empty;
        var text = string.Empty;
        if (messages != null) message = messages.FirstOrDefault() as JSONDataMap;
        if (message != null)
        {
          code = message["code"].AsString(string.Empty);
          text = message["text"].AsString(string.Empty);
        }

        // error found
        if (!state.EqualsIgnoreCase(STATUS_VALID) || code.EqualsIgnoreCase(CODE_INVALID))
        {
          var errMess = StringConsts.SHIPPO_VALIDATE_ADDRESS_INVALID_ERROR.Args(text);
          WriteLog(MessageType.Error, "doValidateAddress()", errMess, relatedMessageID: logID);
          error = new ValidateShippingAddressException(errMess, text);
          return null;
        }

        // no errors
        var corrAddress = getAddressFromJSON(response);
        return corrAddress;
      }

      private ShippingRate doEstimateShippingCost(ShippoSession session, IShippingContext context, Shipment shipment, Guid logID)
      {
        var cred = (ShippoCredentials)session.User.Credentials;
        var sbody = getShipmentBody(shipment);

        // get shipping request
        var request = new WebClient.RequestParams(this)
        {
          Method = HTTPRequestMethod.POST,
          ContentType = ContentType.JSON,
          Headers = new Dictionary<string, string>
            {
              { HDR_AUTHORIZATION, HDR_AUTHORIZATION_TOKEN.Args(cred.PrivateToken) }
            },
          Body = sbody.ToJSON(JSONWritingOptions.Compact)
        };

        var response = WebClient.GetJson(new Uri(URI_API_BASE + URI_SHIPMENTS), request);

        WriteLog(MessageType.Info, "doEstimateShippingCost()", response.ToJSON(), relatedMessageID: logID);

        checkResponse(response);

        var rates = response["rates_list"] as JSONDataArray;
        if (rates == null) return null;

        var bestApprRate = new ShippingRate
                           {
                             CarrierID=shipment.Carrier.Name,
                             ServiceID=shipment.Service.Name,
                             PackageID=shipment.Package?.Name
                           };
        var bestAltRate = new ShippingRate
                          {
                            CarrierID=shipment.Carrier.Name,
                            ServiceID=shipment.Service.Name,
                            PackageID=shipment.Package?.Name,
                            IsAlternative = true
                          };

        // try to find rate with requested carrier/service/package (i.e. "appropriate") with the best price
        // if no appropriate rate found, return alternative rate (also with the best price)
        foreach (JSONDataMap rate in rates)
        {
          var carrierID = rate["carrier_account"].AsString();
          var serviceID = rate["servicelevel_token"].AsString();
          var cost = new Financial.Amount(rate["currency_local"].AsString(), rate["amount_local"].AsDecimal());

          if (shipment.Carrier.Name.EqualsIgnoreCase(carrierID) &&
              shipment.Service.Name.EqualsIgnoreCase(serviceID))
          {
            if (bestApprRate.Cost==null ||
                (bestApprRate.Cost.Value.CurrencyISO.EqualsIgnoreCase(cost.CurrencyISO) && // todo: multiple currencies
                 bestApprRate.Cost.Value.Value > cost.Value))
              bestApprRate.Cost = cost;
          }

          if (bestAltRate.Cost==null ||
              (bestAltRate.Cost.Value.CurrencyISO.EqualsIgnoreCase(cost.CurrencyISO) && // todo: multiple currencies
               bestAltRate.Cost.Value.Value > cost.Value))
            bestAltRate.Cost = cost;
        }

        return (bestApprRate.Cost != null) ? bestApprRate : bestAltRate;
      }

      private JSONDataMap doGetRate(ShippoSession session, string rateID, Guid logID)
      {
        try
        {
          var cred = (ShippoCredentials)session.User.Credentials;

          var request = new WebClient.RequestParams(this)
          {
            Method = HTTPRequestMethod.GET,
            ContentType = ContentType.JSON,
            Headers = new Dictionary<string, string>
              {
                { HDR_AUTHORIZATION, HDR_AUTHORIZATION_TOKEN.Args(cred.PrivateToken) }
              }
          };

          var response = WebClient.GetJson(new Uri((URI_API_BASE + URI_RATES).Args(rateID)), request);

          checkResponse(response);

          return response;
        }
        catch (Exception ex)
        {
          var error = ShippingException.ComposeError(ex.Message, ex);
          WriteLog(MessageType.Error, "getRate()", StringConsts.SHIPPO_CREATE_LABEL_ERROR, error, relatedMessageID: logID);
          return null;
        }
      }


      private Address getAddressFromJSON(JSONDataMap map)
      {
        if (map==null) return null;

        var result = new Address();
        result.PersonName = map["name"].AsString();
        result.Line1      = map["street1"].AsString();
        result.Line2      = map["street2"].AsString();
        result.City       = map["city"].AsString();
        result.Region     = map["state"].AsString();
        result.Postal     = AddressComparator.GetPostalMainPart(map["zip"].AsString());
        result.Country    = Azos.Standards.Countries_ISO3166_1.Normalize3(map["country"].AsString());
        result.Phone      = map["phone"].AsString();
        result.EMail      = map["email"].AsString();
        result.Company    = map["company"].AsString();

        return result;
      }

      private JSONDataMap getCreateLabelRequestBody(ShippoSession session, Shipment shipment)
      {
        var isReturn = shipment.LabelIDForReturn.IsNotNullOrWhiteSpace();

        var body = new JSONDataMap();
        body["carrier_account"] = shipment.Carrier.Name;
        body["servicelevel_token"] = shipment.Service.Name;
        body["label_file_type"] = FORMATS[shipment.LabelFormat];
        body["async"] = false;

        var shpm = new JSONDataMap();
        shpm["object_purpose"] = PURCHASE_PURPOSE;
        shpm["parcel"] = getParcelBody(shipment);
        shpm["address_from"] = getAddressBody(shipment.FromAddress);
        shpm["address_to"] = getAddressBody(shipment.ToAddress);
        if (!isReturn && (shipment.ReturnAddress != null))
          shpm["address_return"] = getAddressBody(shipment.ReturnAddress);

        if (isReturn) shpm["return_of"] = shipment.LabelIDForReturn;

        body["shipment"] = shpm;

        return body;
      }

      private JSONDataMap getParcelBody(Shipment shipment)
      {
        var parcel = new JSONDataMap();
        if (shipment.Package != null)
          parcel["template"] = shipment.Package.Name;

        parcel["distance_unit"] = DIST_UNITS[shipment.DistanceUnit];
        parcel["length"] = shipment.Length.ToString(CultureInfo.InvariantCulture);
        parcel["width"] = shipment.Width.ToString(CultureInfo.InvariantCulture);
        parcel["height"] = shipment.Height.ToString(CultureInfo.InvariantCulture);
        parcel["mass_unit"] = WEIGHT_UNITS[shipment.WeightUnit];
        parcel["weight"] = shipment.Weight.ToString(CultureInfo.InvariantCulture);

        return parcel;
      }

      private JSONDataMap getAddressBody(Address addr)
      {
        var result = new JSONDataMap();

        result["object_purpose"] = PURCHASE_PURPOSE;
        result["name"] = addr.PersonName;
        result["country"] = Azos.Standards.Countries_ISO3166_1.Normalize2(addr.Country);
        result["street1"] = addr.Line1;
        result["street2"] = addr.Line2;
        result["city"] = addr.City;
        result["state"] = addr.Region;
        result["zip"] = addr.Postal;
        result["phone"] = addr.Phone;
        result["email"] = addr.EMail;
        result["company"] = addr.Company;

        return result;
      }

      private JSONDataMap getShipmentBody(Shipment shipment)
      {
        var isReturn = shipment.LabelIDForReturn.IsNotNullOrWhiteSpace();

        var result = new JSONDataMap();
        result["object_purpose"] = PURCHASE_PURPOSE;
        result["parcel"] = getParcelBody(shipment);
        result["address_from"] = getAddressBody(shipment.FromAddress);
        result["address_to"] = getAddressBody(shipment.ToAddress);
        if (!isReturn && (shipment.ReturnAddress != null))
          result["address_return"] = getAddressBody(shipment.ReturnAddress);
        if (isReturn) result["return_of"] = shipment.LabelIDForReturn;
        result["async"] = false;

        return result;
      }


      private void checkResponse(JSONDataMap response)
      {
        if (response == null) throw new ShippingException("checkResponse(response=null)");

        if (!response["object_state"].AsString().EqualsIgnoreCase(STATUS_VALID) ||
            !response["object_status"].AsString(STATUS_SUCCESS).EqualsIgnoreCase(STATUS_SUCCESS))
        {
          string text = null;
          var messages = response["messages"] as JSONDataArray;
          if (messages != null && messages.Any())
          {
            var message = (JSONDataMap)messages.First();
            text = message["text"].AsString(string.Empty);
          }

          if (text.IsNullOrWhiteSpace()) text = StringConsts.SHIPPO_OPERATION_FAILED;

          throw new ShippingException(text);
        }
      }

    #endregion
  }
}
