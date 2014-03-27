﻿using Breeze.Sharp.Core;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

namespace Breeze.Sharp {

  internal class CsdlMetadataProcessor {

    public CsdlMetadataProcessor() {
      
    }
    
    public void ProcessMetadata(MetadataStore metadataStore, String jsonMetadata) {
      _metadataStore = metadataStore;
      var json = (JObject)JsonConvert.DeserializeObject(jsonMetadata);
      _schema = json["schema"];
      _namespace = (String)_schema["namespace"];

      var mapping = (String)_schema["cSpaceOSpaceMapping"];
      if (mapping != null) {
        var tmp = (JArray)JsonConvert.DeserializeObject(mapping);
        _cSpaceOSpaceMap = tmp.ToDictionary(v => (String)v[0], v => (String)v[1]);
      }

      var entityTypes = ToEnumerable(_schema["entityType"]).Cast<JObject>()
        .Select(ParseCsdlEntityType).ToList();

      // fixup all related nav props.
      entityTypes.ForEach(et => {
        if (et.KeyProperties.Count == 0) {
          throw new Exception("Unable to locate key property for EntityType: " + et.Name);
        }
        et.UpdateNavigationProperties();
      });
      

      var complexTypes = ToEnumerable(_schema["complexType"]).Cast<JObject>()
        .Select(ParseCsdlComplexType).ToList();

      var entityContainer = _schema["entityContainer"];
      if (entityContainer != null) {
        var entitySets = ToEnumerable(entityContainer["entitySet"]).Cast<JObject>().ToList();
        entitySets.ForEach(es => {
          var clientEtName = GetClientTypeNameFromClrTypeName((String) es["entityType"]);
          var entityType = _metadataStore.GetEntityType(clientEtName);
          var resourceName = (String) es["name"];
          _metadataStore.AddResourceName(resourceName, entityType, true);
        });
      }
    }

    private NamingConvention NamingConvention {
      get { return MetadataStore.Instance.NamingConvention; }
    }


    private EntityType ParseCsdlEntityType(JObject csdlEntityType) {
      var abstractVal = (String)csdlEntityType["abstract"];
      var baseTypeVal = (String)csdlEntityType["baseType"];
      var shortNameVal = (String)csdlEntityType["name"];
      var isAbstract = abstractVal == "true";
      var etName = GetClientTypeNameFromShortName(shortNameVal);
      var entityType = MetadataStore.Instance.GetEntityType(etName);
      
      entityType.IsAbstract = isAbstract;
      EntityType baseEntityType = null;
      if (baseTypeVal != null) {
        var baseEtName = GetClientTypeNameFromClrTypeName(baseTypeVal);
        baseEntityType = _metadataStore.GetEntityType(baseEtName, true);
      }
      CompleteParseCsdlEntityType(entityType, csdlEntityType, baseEntityType);
      // entityType may or may not have been added to the metadataStore at this point.
      return entityType;
    }

    private void CompleteParseCsdlEntityType(EntityType entityType, JObject csdlEntityType, EntityType baseEntityType) {
      var baseKeyNamesOnServer = new List<string>();
      if (baseEntityType != null) {
        entityType.BaseEntityType = baseEntityType;
        entityType.AutoGeneratedKeyType = baseEntityType.AutoGeneratedKeyType;
        baseKeyNamesOnServer = baseEntityType.KeyProperties.Select(dp => dp.NameOnServer).ToList();
        
      }
      var keyVal = csdlEntityType["key"];
      var keyNamesOnServer = keyVal == null
        ? new List<String>()
        : ToEnumerable(keyVal["propertyRef"]).Select(x => (String)x["name"]).ToList();
      
      keyNamesOnServer.AddRange(baseKeyNamesOnServer);

      ToEnumerable(csdlEntityType["property"]).ForEach(csdlDataProp => {
        ParseCsdlDataProperty(entityType, (JObject)csdlDataProp, keyNamesOnServer);
      });

      ToEnumerable(csdlEntityType["navigationProperty"]).ForEach(csdlNavProp => {
        ParseCsdlNavigationProperty(entityType, (JObject)csdlNavProp);
      });

    
    }

    private DataProperty ParseCsdlDataProperty(StructuralType parentType, JObject csdlProperty, List<String> keyNamesOnServer) {
      DataProperty dp;
      var typeParts = ExtractTypeNameParts(csdlProperty);

      if (typeParts.Length == 2) {
        dp = ParseCsdlSimpleProperty(parentType, csdlProperty, keyNamesOnServer);
      } else {
        if (IsEnumType(csdlProperty)) {
          dp = ParseCsdlSimpleProperty(parentType, csdlProperty, keyNamesOnServer);
          dp.EnumTypeName = (String)csdlProperty["type"];
        } else {
          dp = ParseCsdlComplexProperty(parentType, csdlProperty);
        }
      }

      if (dp != null) {
        AddValidators(dp);
      }
      return dp;
    }

    private DataProperty ParseCsdlSimpleProperty(StructuralType parentType, JObject csdlProperty, List<String> keyNamesOnServer) {

      var typeVal = (String)csdlProperty["type"];
      var nameVal = (String)csdlProperty["name"];
      var nullableVal = (String)csdlProperty["nullable"];
      var maxLengthVal = (String)csdlProperty["maxLength"];
      var concurrencyModeVal = (String)csdlProperty["concurrencyMode"];

      var dataType = DataType.FromEdmType(typeVal);
      if (dataType == DataType.Undefined) {
        parentType.Warnings.Add("Unable to recognize DataType for property: " + nameVal + " DateType: " + typeVal);
      }

      var isNullable = nullableVal == "true" || nullableVal == null;
      var entityType = parentType as EntityType;
      bool isPartOfKey = false;
      bool isAutoIncrementing = false;
      if (entityType != null) {
        isPartOfKey = keyNamesOnServer != null && keyNamesOnServer.IndexOf(nameVal) >= 0;
        if (isPartOfKey && entityType.AutoGeneratedKeyType == AutoGeneratedKeyType.None) {
          if (IsIdentityProperty(csdlProperty)) {
            isAutoIncrementing = true;
            entityType.AutoGeneratedKeyType = AutoGeneratedKeyType.Identity;
          }
        }
      }

      Object defaultValue;
      var rawDefaultValue = csdlProperty["defaultValue"];
      if (rawDefaultValue == null) {
        defaultValue = isNullable ? null : dataType.DefaultValue;
      } else {
        defaultValue = rawDefaultValue.ToObject(dataType.ClrType);
      }

      // TODO: nit - don't set maxLength if null;
      var maxLength = (maxLengthVal == null || maxLengthVal == "Max") ? (Int64?)null : Int64.Parse(maxLengthVal);
      var concurrencyMode = concurrencyModeVal == "fixed" ? ConcurrencyMode.Fixed : ConcurrencyMode.None;

      var dpName = MetadataStore.Instance.NamingConvention.ServerPropertyNameToClient(nameVal);
      var dp = parentType.GetDataProperty(dpName);
      if (dp == null) {
        throw new Exception("Unable to locate a DataProperty named: " + dpName + " on the EntityType: " + parentType.Name);
      }
      dp.Check(dp.DataType, dataType, "DataType");
      dp.Check(dp.IsScalar, true, "IsScalar");

      dp.IsPartOfKey = isPartOfKey;
      dp.IsNullable = isNullable;
      dp.MaxLength = maxLength;
      dp.DefaultValue = defaultValue;
        // fixedLength: fixedLength,
      dp.ConcurrencyMode = concurrencyMode;
      dp.IsAutoIncrementing = isAutoIncrementing;

      if (dataType == DataType.Undefined) {
        dp.RawTypeName = typeVal;
      }
      return dp;
    }

    private DataProperty ParseCsdlComplexProperty(StructuralType parentType, JObject csdlProperty) {
      // Complex properties are never nullable ( per EF specs)
      // var isNullable = csdlProperty.nullable === 'true' || csdlProperty.nullable == null;

      var complexTypeName = GetClientTypeNameFromClrTypeName((String)csdlProperty["type"]);
      // can't set the name until we go thru namingConventions and these need the dp.
      var nameOnServer = (String)csdlProperty["name"];
      var name = NamingConvention.ServerPropertyNameToClient(nameOnServer);
      var dp = parentType.GetDataProperty(name);

      dp.Check(dp.ComplexType.Name, complexTypeName, "ComplexTypeName");

      return dp;
    }

    private NavigationProperty ParseCsdlNavigationProperty(EntityType parentType, JObject csdlProperty) {
      var association = GetAssociation(csdlProperty);
      var toRoleVal = (String)csdlProperty["toRole"];
      var fromRoleVal = (String)csdlProperty["fromRole"];
      var nameOnServer = (String)csdlProperty["name"];
      var toEnd = ToEnumerable(association["end"]).FirstOrDefault(end => (String)end["role"] == toRoleVal);
      var isScalar = (String)toEnd["multiplicity"] != "*";
      var dataEtName = GetClientTypeNameFromClrTypeName((String)toEnd["type"]);
      var constraintVal = association["referentialConstraint"];
      if (constraintVal == null) {
        return null;
        // TODO: Revisit this later - right now we just ignore many-many and assocs with missing constraints.
        //
        // if (association.end[0].multiplicity == "*" && association.end[1].multiplicity == "*") {
        //    // many to many relation
        //    ???
        // } else {
        //    throw new Error("Foreign Key Associations must be turned on for this model");
        // }
      }
      
      var name = NamingConvention.ServerPropertyNameToClient(nameOnServer);
      var np = parentType.GetNavigationProperty(name);

      np.Check(np.EntityType.Name, dataEtName, "EntityTypeName");
      np.Check(np.IsScalar, isScalar, "IsScalar");

      np.AssociationName = (String) association["name"];
      
      var principal = constraintVal["principal"];
      var dependent = constraintVal["dependent"];

      var propRefs = ToEnumerable(dependent["propertyRef"]);
      var fkNames = propRefs.Select(pr => (String)pr["name"]).ToSafeList();
      if (fromRoleVal == (String)principal["role"]) {
        np.SetInvFkNames(fkNames, true);
      } else {
        np.SetFkNames(fkNames, true);
      }


      return np;

    }

    private JObject GetAssociation(JObject csdlNavProperty) {
      var assocsVal = _schema["association"];
      if (assocsVal == null) return null;

      var relationshipVal = (String)csdlNavProperty["relationship"];
      var assocName = ParseClrTypeName(relationshipVal).ShortName;

      var association = ToEnumerable(assocsVal).FirstOrDefault(assoc => (String)assoc["name"] == assocName);

      return (JObject)association;
    }

    private ComplexType ParseCsdlComplexType(JObject csdlComplexType) {
      var nameVal = (String)csdlComplexType["name"];
      var clientTypeName = GetClientTypeNameFromShortName(nameVal);
      var complexType = MetadataStore.Instance.GetComplexType(clientTypeName);

      ToEnumerable(csdlComplexType["property"])
        .ForEach(prop => ParseCsdlDataProperty(complexType, (JObject)prop, null));

      return complexType;
    }

    private string GetClientTypeNameFromShortName(string serverShortName) {
      var ns = GetNamespaceFor(serverShortName);
      var clientTypeName = new TypeNameInfo(serverShortName, ns).ToClient().Name;
      return clientTypeName;
    }

    private string GetClientTypeNameFromClrTypeName(string serverClrTypeName) {
      var clientTypeName = ParseClrTypeName(serverClrTypeName).ToClient().Name;
      return clientTypeName;
    }

    private TypeNameInfo ParseClrTypeName(String clrTypeName) {
      if (String.IsNullOrEmpty(clrTypeName)) return null;
      if (clrTypeName.StartsWith(MetadataStore.ANONTYPE_PREFIX)) {
        return new TypeNameInfo(clrTypeName, String.Empty, true);
      }

      var entityTypeNameNoAssembly = clrTypeName.Split(',')[0];
      var nameParts = entityTypeNameNoAssembly.Split('.');
      if (nameParts.Length > 1) {
        var shortName = nameParts[nameParts.Length - 1];
        // HACK: this call is why we can't use TypeNameInfo.FromClrTypeName.
        var ns = GetNamespaceFor(shortName);
        return new TypeNameInfo(shortName, ns);
      } else {
        return new TypeNameInfo(clrTypeName, String.Empty);
      }
    }

    private String GetNamespaceFor(String shortName) {

      if (_cSpaceOSpaceMap != null) {
        var cSpaceName = _namespace + "." + shortName;
        String oSpaceName;
        if (_cSpaceOSpaceMap.TryGetValue(cSpaceName, out oSpaceName)) {
          var ns = oSpaceName.Substring(0, oSpaceName.Length - (shortName.Length + 1));
          return ns;
        }
      }
      return _namespace;
    }

    private bool IsIdentityProperty(JObject csdlProperty) {

      var subProp = csdlProperty.Properties().FirstOrDefault(p => p.Name.IndexOf("StoreGeneratedPattern") > 0);
      if (subProp != null) {
        return subProp.Value.ToObject<String>() == "Identity";
      } else {
        // see if Odata feed
        var extensionsVal = csdlProperty["extensions"];
        if (extensionsVal == null) return false;
        // TODO: NOT YET TESTED
        var identityExtn = ToEnumerable(extensionsVal).FirstOrDefault(extn => {
          return (String)extn["name"] == "StoreGeneratedPattern" && (String)extn["value"] == "Identity";
        });
        return identityExtn != null;
      }
    }

    private void AddValidators(DataProperty dp) {
      if (!dp.IsNullable) {
        dp._validators.Add(new RequiredValidator().Intern());
      }
      if (dp.MaxLength.HasValue) {
        var vr = new MaxLengthValidator( (Int32) dp.MaxLength.Value).Intern();
        dp._validators.Add(vr);
      }
    }

    private bool IsEnumType(JObject csdlProperty) {
      var enumTypeVal = _schema["enumType"];
      if (enumTypeVal == null) return false;
      var enumTypes = ToEnumerable(enumTypeVal);
      var typeParts = ExtractTypeNameParts(csdlProperty);
      var baseTypeName = typeParts[typeParts.Length - 1];
      return enumTypes.Any(enumType => ((String)enumType["name"] == baseTypeName));
    }

    private String[] ExtractTypeNameParts(JObject csdlProperty) {
      var typeParts = ((String)csdlProperty["type"]).Split('.');
      return typeParts;
    }

    private IEnumerable<T> ToEnumerable<T>(T d) {
      if (d == null) {
        return Enumerable.Empty<T>();
      } else if (d.GetType() == typeof(JArray)) {
        return ((IEnumerable)d).Cast<T>();
      } else {
        return new T[] { d };
      }
    }

    private JToken _schema;
    private String _namespace;
    private MetadataStore _metadataStore;
    private Dictionary<String, String> _cSpaceOSpaceMap;




  }
}
