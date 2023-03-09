﻿// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using AutoMapper.Internal;
using Newtonsoft.Json;
using osu.Framework.Bindables;
using osu.Framework.Extensions.TypeExtensions;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Localisation;
using osu.Framework.Testing;
using osu.Game.Configuration;
using osu.Game.Extensions;
using osu.Game.Rulesets.UI;
using osu.Game.Utils;

namespace osu.Game.Rulesets.Mods
{
    /// <summary>
    /// The base class for gameplay modifiers.
    /// </summary>
    [ExcludeFromDynamicCompile]
    public abstract class Mod : IMod, IEquatable<Mod>, IDeepCloneable<Mod>
    {
        [JsonIgnore]
        public abstract string Name { get; }

        public abstract string Acronym { get; }

        [JsonIgnore]
        public virtual IconUsage? Icon => null;

        [JsonIgnore]
        public virtual ModType Type => ModType.Fun;

        [JsonIgnore]
        public abstract LocalisableString Description { get; }

        /// <summary>
        /// The tooltip to display for this mod when used in a <see cref="ModIcon"/>.
        /// </summary>
        /// <remarks>
        /// Differs from <see cref="Name"/>, as the value of attributes (AR, CS, etc) changeable via the mod
        /// are displayed in the tooltip.
        /// </remarks>
        [JsonIgnore]
        public string IconTooltip
        {
            get
            {
                string description = SettingDescription;

                return string.IsNullOrEmpty(description) ? Name : $"{Name} ({description})";
            }
        }

        /// <summary>
        /// The description of editable settings of a mod to use in the <see cref="IconTooltip"/>.
        /// </summary>
        /// <remarks>
        /// Parentheses are added to the tooltip, surrounding the value of this property. If this property is <c>string.Empty</c>,
        /// the tooltip will not have parentheses.
        /// </remarks>
        public virtual string SettingDescription
        {
            get
            {
                var tooltipTexts = new List<string>();

                foreach ((SettingSourceAttribute attr, PropertyInfo property) in this.GetOrderedSettingsSourceProperties())
                {
                    var bindable = (IBindable)property.GetValue(this)!;

                    if (!bindable.IsDefault)
                        tooltipTexts.Add($"{attr.Label} {bindable}");
                }

                return string.Join(", ", tooltipTexts.Where(s => !string.IsNullOrEmpty(s)));
            }
        }

        /// <summary>
        /// The score multiplier of this mod.
        /// </summary>
        [JsonIgnore]
        public abstract double ScoreMultiplier { get; }

        /// <summary>
        /// Returns true if this mod is implemented (and playable).
        /// </summary>
        [JsonIgnore]
        public virtual bool HasImplementation => this is IApplicableMod;

        [JsonIgnore]
        public virtual bool UserPlayable => true;

        [JsonIgnore]
        public virtual bool ValidForMultiplayer => true;

        [JsonIgnore]
        public virtual bool ValidForMultiplayerAsFreeMod => true;

        /// <summary>
        /// Whether this mod requires configuration to apply changes to the game.
        /// </summary>
        [JsonIgnore]
        public virtual bool RequiresConfiguration => false;

        /// <summary>
        /// The mods this mod cannot be enabled with.
        /// </summary>
        [JsonIgnore]
        public virtual Type[] IncompatibleMods => Array.Empty<Type>();

        private IReadOnlyList<IBindable>? settingsBacking;

        /// <summary>
        /// A list of the all <see cref="IBindable"/> settings within this mod.
        /// </summary>
        internal IReadOnlyList<IBindable> Settings =>
            settingsBacking ??= this.GetSettingsSourceProperties()
                                    .Select(p => p.Item2.GetValue(this))
                                    .Cast<IBindable>()
                                    .ToList();

        /// <summary>
        /// Whether all settings in this mod are set to their default state.
        /// </summary>
        protected virtual bool UsesDefaultConfiguration => Settings.All(s => s.IsDefault);

        /// <summary>
        /// Creates a copy of this <see cref="Mod"/> initialised to a default state.
        /// </summary>
        public virtual Mod DeepClone()
        {
            var result = (Mod)Activator.CreateInstance(GetType())!;
            result.CopyFrom(this);
            return result;
        }

        /// <summary>
        /// Copies mod setting values from <paramref name="source"/> into this instance, overwriting all existing settings.
        /// </summary>
        /// <param name="source">The mod to copy properties from.</param>
        public void CopyFrom(Mod source)
        {
            if (source.GetType() != GetType())
                throw new ArgumentException($"Expected mod of type {GetType()}, got {source.GetType()}.", nameof(source));

            foreach (var (_, property) in this.GetSettingsSourceProperties())
            {
                var targetBindable = (IBindable)property.GetValue(this)!;
                var sourceBindable = (IBindable)property.GetValue(source)!;

                CopyAdjustedSetting(targetBindable, sourceBindable);
            }
        }

        /// <summary>
        /// Copies all mod setting values sharing same <see cref="MemberInfo.Name"/> from <paramref name="source"/> into this instance.
        /// </summary>
        /// <param name="source">The mod to copy properties from.</param>
        internal void CopySharedSettings(Mod source)
        {
            const string min_value = nameof(BindableNumber<int>.MinValue);
            const string max_value = nameof(BindableNumber<int>.MaxValue);
            const string value = nameof(Bindable<int>.Value);

            Dictionary<string, object> sourceSettings = new Dictionary<string, object>();

            foreach (var (_, sourceProperty) in source.GetSettingsSourceProperties())
            {
                sourceSettings.Add(sourceProperty.Name.ToSnakeCase(), sourceProperty.GetValue(source)!);
            }

            foreach (var (_, targetProperty) in this.GetSettingsSourceProperties())
            {
                object targetSetting = targetProperty.GetValue(this)!;

                if (!sourceSettings.TryGetValue(targetProperty.Name.ToSnakeCase(), out object? sourceSetting))
                    continue;

                if (((IBindable)sourceSetting).IsDefault)
                {
                    // reset to default value if the source is default
                    targetSetting.GetType().GetMethod(nameof(Bindable<int>.SetDefault))!.Invoke(targetSetting, null);
                    continue;
                }

                Type? targetBindableNumberType = getGenericBaseType(targetSetting, typeof(BindableNumber<>));
                Type? sourceBindableNumberType = getGenericBaseType(sourceSetting, typeof(BindableNumber<>));

                if (targetBindableNumberType == null || sourceBindableNumberType == null)
                {
                    if (getGenericBaseType(targetSetting, typeof(Bindable<>))!.GenericTypeArguments.Single() ==
                        getGenericBaseType(sourceSetting, typeof(Bindable<>))!.GenericTypeArguments.Single())
                    {
                        // change settings only if the type is the same
                        setValue(targetSetting, value, getValue(sourceSetting, value));
                    }

                    continue;
                }

                bool rangeOutOfBounds = false;

                Type targetGenericType = targetBindableNumberType.GenericTypeArguments.Single();
                Type sourceGenericType = sourceBindableNumberType.GenericTypeArguments.Single();

                if (!Convert.ToBoolean(getValue(targetSetting, nameof(RangeConstrainedBindable<int>.HasDefinedRange))) ||
                    !Convert.ToBoolean(getValue(sourceSetting, nameof(RangeConstrainedBindable<int>.HasDefinedRange))))
                    // check if we have a range to rescale from and a range to rescale to
                    // if not, copy the raw value
                    rangeOutOfBounds = true;

                double allowedMin = Math.Max(
                    Convert.ToDouble(targetGenericType.GetField("MinValue")!.GetValue(null)),
                    Convert.ToDouble(sourceGenericType.GetField("MinValue")!.GetValue(null))
                );

                double allowedMax = Math.Min(
                    Convert.ToDouble(targetGenericType.GetField("MaxValue")!.GetValue(null)),
                    Convert.ToDouble(sourceGenericType.GetField("MaxValue")!.GetValue(null))
                );

                double targetMin = getValueDouble(targetSetting, min_value);
                double targetMax = getValueDouble(targetSetting, max_value);
                double sourceMin = getValueDouble(sourceSetting, min_value);
                double sourceMax = getValueDouble(sourceSetting, max_value);
                double sourceValue = Math.Clamp(getValueDouble(sourceSetting, value), allowedMin, allowedMax);

                double targetValue = rangeOutOfBounds
                    // keep raw value
                    ? sourceValue
                    // convert value to same ratio
                    : (sourceValue - sourceMin) / (sourceMax - sourceMin) * (targetMax - targetMin) + targetMin;

                setValue(targetSetting, value, Convert.ChangeType(targetValue, targetBindableNumberType.GenericTypeArguments.Single()));

                double getValueDouble(object setting, string name)
                {
                    double settingValue = Convert.ToDouble(getValue(setting, name)!);

                    if (settingValue < allowedMin || settingValue > allowedMax)
                        rangeOutOfBounds = true;

                    return settingValue;
                }
            }

            object? getValue(object setting, string name) =>
                setting.GetType().GetProperty(name)!.GetValue(setting);

            void setValue(object setting, string name, object? newValue) =>
                setting.GetType().GetProperty(name)!.SetValue(setting, newValue);

            Type? getGenericBaseType(object setting, Type genericType) =>
                setting.GetType().GetTypeInheritance().FirstOrDefault(t => t.IsGenericType && t.GetGenericTypeDefinition() == genericType);
        }

        /// <summary>
        /// When creating copies or clones of a Mod, this method will be called
        /// to copy explicitly adjusted user settings from <paramref name="target"/>.
        /// The base implementation will transfer the value via <see cref="Bindable{T}.Parse"/>
        /// or by binding and unbinding (if <paramref name="source"/> is an <see cref="IBindable"/>)
        /// and should be called unless replaced with custom logic.
        /// </summary>
        /// <param name="target">The target bindable to apply the adjustment to.</param>
        /// <param name="source">The adjustment to apply.</param>
        internal virtual void CopyAdjustedSetting(IBindable target, object source)
        {
            if (source is IBindable sourceBindable)
            {
                // copy including transfer of default values.
                target.BindTo(sourceBindable);
                target.UnbindFrom(sourceBindable);
            }
            else
            {
                if (!(target is IParseable parseable))
                    throw new InvalidOperationException($"Bindable type {target.GetType().ReadableName()} is not {nameof(IParseable)}.");

                parseable.Parse(source);
            }
        }

        public bool Equals(IMod? other) => other is Mod them && Equals(them);

        public bool Equals(Mod? other)
        {
            if (ReferenceEquals(null, other)) return false;
            if (ReferenceEquals(this, other)) return true;

            return GetType() == other.GetType() &&
                   Settings.SequenceEqual(other.Settings, ModSettingsEqualityComparer.Default);
        }

        public override int GetHashCode()
        {
            var hashCode = new HashCode();

            hashCode.Add(GetType());

            foreach (var setting in Settings)
                hashCode.Add(setting.GetUnderlyingSettingValue());

            return hashCode.ToHashCode();
        }

        /// <summary>
        /// Reset all custom settings for this mod back to their defaults.
        /// </summary>
        public virtual void ResetSettingsToDefaults() => CopyFrom((Mod)Activator.CreateInstance(GetType())!);

        private class ModSettingsEqualityComparer : IEqualityComparer<IBindable>
        {
            public static ModSettingsEqualityComparer Default { get; } = new ModSettingsEqualityComparer();

            public bool Equals(IBindable? x, IBindable? y)
            {
                object? xValue = x?.GetUnderlyingSettingValue();
                object? yValue = y?.GetUnderlyingSettingValue();

                return EqualityComparer<object>.Default.Equals(xValue, yValue);
            }

            public int GetHashCode(IBindable obj) => obj.GetUnderlyingSettingValue().GetHashCode();
        }
    }
}
