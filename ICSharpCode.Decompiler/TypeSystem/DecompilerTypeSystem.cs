﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ICSharpCode.NRefactory;
using ICSharpCode.NRefactory.TypeSystem;
using ICSharpCode.NRefactory.TypeSystem.Implementation;
using Mono.Cecil;

namespace ICSharpCode.Decompiler
{
	/// <summary>
	/// Manages the NRefactory type system for the decompiler.
	/// </summary>
	/// <remarks>
	/// This class is thread-safe.
	/// </remarks>
	public class DecompilerTypeSystem : IDecompilerTypeSystem
	{
		readonly ModuleDefinition moduleDefinition;
		readonly ICompilation compilation;
		readonly ITypeResolveContext context;

		/// <summary>
		/// CecilLoader used for converting cecil type references to ITypeReference.
		/// May only be accessed within lock(typeReferenceCecilLoader).
		/// </summary>
		readonly CecilLoader typeReferenceCecilLoader = new CecilLoader();

		/// <summary>
		/// Dictionary for NRefactory->Cecil lookup. Only contains entities from the main module.
		/// May only be accessed within lock(entityDict)
		/// </summary>
		Dictionary<IUnresolvedEntity, MemberReference> entityDict = new Dictionary<IUnresolvedEntity, MemberReference>();

		Dictionary<FieldReference, IField> fieldLookupCache = new Dictionary<FieldReference, IField>();
		Dictionary<PropertyReference, IProperty> propertyLookupCache = new Dictionary<PropertyReference, IProperty>();
		Dictionary<MethodReference, IMethod> methodLookupCache = new Dictionary<MethodReference, IMethod>();
		Dictionary<EventReference, IEvent> eventLookupCache = new Dictionary<EventReference, IEvent>();
		
		public DecompilerTypeSystem(ModuleDefinition moduleDefinition)
		{
			if (moduleDefinition == null)
				throw new ArgumentNullException("moduleDefinition");
			this.moduleDefinition = moduleDefinition;
			CecilLoader mainAssemblyCecilLoader = new CecilLoader { IncludeInternalMembers = true, LazyLoad = true, OnEntityLoaded = StoreMemberReference, ShortenInterfaceImplNames = false };
			CecilLoader referencedAssemblyCecilLoader = new CecilLoader { IncludeInternalMembers = true, LazyLoad = true, ShortenInterfaceImplNames = false };
			typeReferenceCecilLoader.SetCurrentModule(moduleDefinition);
			IUnresolvedAssembly mainAssembly = mainAssemblyCecilLoader.LoadModule(moduleDefinition);
			var referencedAssemblies = new List<IUnresolvedAssembly>();
			foreach (var asmRef in moduleDefinition.AssemblyReferences) {
				var asm = moduleDefinition.AssemblyResolver.Resolve(asmRef);
				if (asm != null)
					referencedAssemblies.Add(referencedAssemblyCecilLoader.LoadAssembly(asm));
			}
			compilation = new SimpleCompilation(mainAssembly, referencedAssemblies);
			context = new SimpleTypeResolveContext(compilation.MainAssembly);
		}

		public ICompilation Compilation {
			get { return compilation; }
		}
		
		public IAssembly MainAssembly {
			get { return compilation.MainAssembly; }
		}

		public ModuleDefinition ModuleDefinition {
			get { return moduleDefinition; }
		}

		void StoreMemberReference(IUnresolvedEntity entity, MemberReference mr)
		{
			// This is a callback from the type system, which is multi-threaded and may be accessed externally
			lock (entityDict)
				entityDict[entity] = mr;
		}

		MemberReference GetCecil(IUnresolvedEntity member)
		{
			lock (entityDict) {
				MemberReference mr;
				if (member != null && entityDict.TryGetValue(member, out mr))
					return mr;
				return null;
			}
		}

		/// <summary>
		/// Retrieves the Cecil member definition for the specified member.
		/// </summary>
		/// <remarks>
		/// Returns null if the member is not defined in the module being decompiled.
		/// </remarks>
		public MemberReference GetCecil(IMember member)
		{
			if (member == null)
				return null;
			return GetCecil(member.UnresolvedMember);
		}

		/// <summary>
		/// Retrieves the Cecil type definition.
		/// </summary>
		/// <remarks>
		/// Returns null if the type is not defined in the module being decompiled.
		/// </remarks>
		public TypeDefinition GetCecil(ITypeDefinition typeDefinition)
		{
			if (typeDefinition == null)
				return null;
			return GetCecil(typeDefinition.Parts[0]) as TypeDefinition;
		}

		#region Resolve Type
		public IType Resolve(TypeReference typeReference)
		{
			if (typeReference == null)
				return SpecialType.UnknownType;
			ITypeReference typeRef;
			if (typeReference is PinnedType) {
				typeRef = new NRefactory.TypeSystem.ByReferenceType(Resolve(((PinnedType)typeReference).ElementType)).ToTypeReference();
			} else {
				lock (typeReferenceCecilLoader)
					typeRef = typeReferenceCecilLoader.ReadTypeReference(typeReference);
			}
			return typeRef.Resolve(context);
		}
		#endregion

		#region Resolve Field
		public IField Resolve(FieldReference fieldReference)
		{
			if (fieldReference == null)
				throw new ArgumentNullException("fieldReference");
			lock (fieldLookupCache) {
				IField field;
				if (!fieldLookupCache.TryGetValue(fieldReference, out field)) {
					field = FindNonGenericField(fieldReference);
					if (fieldReference.DeclaringType.IsGenericInstance) {
						var git = (GenericInstanceType)fieldReference.DeclaringType;
						var typeArguments = git.GenericArguments.SelectArray(Resolve);
						field = (IField)field.Specialize(new TypeParameterSubstitution(typeArguments, null));
					}
					fieldLookupCache.Add(fieldReference, field);
				}
				return field;
			}
		}

		IField FindNonGenericField(FieldReference fieldReference)
		{
			ITypeDefinition typeDef = Resolve(fieldReference.DeclaringType).GetDefinition();
			if (typeDef == null)
				return CreateFakeField(fieldReference);
			foreach (IField field in typeDef.Fields)
				if (field.Name == fieldReference.Name)
					return field;
			return CreateFakeField(fieldReference);
		}

		IField CreateFakeField(FieldReference fieldReference)
		{
			var declaringType = Resolve(fieldReference.DeclaringType);
			var f = new DefaultUnresolvedField();
			f.Name = fieldReference.Name;
			f.ReturnType = typeReferenceCecilLoader.ReadTypeReference(fieldReference.FieldType);
			return new ResolvedFakeField(f, context.WithCurrentTypeDefinition(declaringType.GetDefinition()), declaringType);
		}

		class ResolvedFakeField : DefaultResolvedField
		{
			readonly IType declaringType;

			public ResolvedFakeField(DefaultUnresolvedField unresolved, ITypeResolveContext parentContext, IType declaringType)
				: base(unresolved, parentContext)
			{
				this.declaringType = declaringType;
			}

			public override IType DeclaringType
			{
				get { return declaringType; }
			}
		}
		#endregion

		#region Resolve Method
		public IMethod Resolve(MethodReference methodReference)
		{
			if (methodReference == null)
				throw new ArgumentNullException("methodReference");
			lock (methodLookupCache) {
				IMethod method;
				if (!methodLookupCache.TryGetValue(methodReference, out method)) {
					method = FindNonGenericMethod(methodReference.GetElementMethod());
					if (methodReference.IsGenericInstance || methodReference.DeclaringType.IsGenericInstance) {
						IList<IType> classTypeArguments = null;
						IList<IType> methodTypeArguments = null;
						if (methodReference.IsGenericInstance) {
							var gim = ((GenericInstanceMethod)methodReference);
							methodTypeArguments = gim.GenericArguments.SelectArray(Resolve);
						}
						if (methodReference.DeclaringType.IsGenericInstance) {
							var git = (GenericInstanceType)methodReference.DeclaringType;
							classTypeArguments = git.GenericArguments.SelectArray(Resolve);
						}
						method = method.Specialize(new TypeParameterSubstitution(classTypeArguments, methodTypeArguments));
					}
					methodLookupCache.Add(methodReference, method);
				}
				return method;
			}
		}

		IMethod FindNonGenericMethod(MethodReference methodReference)
		{
			ITypeDefinition typeDef = Resolve(methodReference.DeclaringType).GetDefinition();
			if (typeDef == null)
				return CreateFakeMethod(methodReference);
			IEnumerable<IMethod> methods;
			if (methodReference.Name == ".ctor") {
				methods = typeDef.GetConstructors();
			} else if (methodReference.Name == ".cctor") {
				return typeDef.Methods.FirstOrDefault(m => m.IsConstructor && m.IsStatic);
			} else {
				methods = typeDef.GetMethods(m => m.Name == methodReference.Name, GetMemberOptions.IgnoreInheritedMembers)
					.Concat(typeDef.GetAccessors(m => m.Name == methodReference.Name, GetMemberOptions.IgnoreInheritedMembers));
			}
			foreach (var method in methods) {
				if (GetCecil(method) == methodReference)
					return method;
			}
			var parameterTypes = methodReference.Parameters.SelectArray(p => Resolve(p.ParameterType));
			var returnType = Resolve(methodReference.ReturnType);
			foreach (var method in methods) {
				if (method.TypeParameters.Count != methodReference.GenericParameters.Count)
					continue;
				if (!CompareSignatures(method.Parameters, parameterTypes) || !CompareTypes(method.ReturnType, returnType))
					continue;
				return method;
			}
			return CreateFakeMethod(methodReference);
		}

		static bool CompareTypes(IType a, IType b)
		{
			IType type1 = DummyTypeParameter.NormalizeAllTypeParameters(a);
			IType type2 = DummyTypeParameter.NormalizeAllTypeParameters(b);
			return type1.Equals(type2);
		}
		
		static bool CompareSignatures(IList<IParameter> parameters, IType[] parameterTypes)
		{
			if (parameterTypes.Length != parameters.Count)
				return false;
			for (int i = 0; i < parameterTypes.Length; i++) {
				if (!CompareTypes(parameterTypes[i], parameters[i].Type))
					return false;
			}
			return true;
		}

		/// <summary>
		/// Create a dummy IMethod from the specified MethodReference
		/// </summary>
		IMethod CreateFakeMethod(MethodReference methodReference)
		{
			var declaringTypeReference = typeReferenceCecilLoader.ReadTypeReference(methodReference.DeclaringType);
			var m = new DefaultUnresolvedMethod();
			if (methodReference.Name == ".ctor" || methodReference.Name == ".cctor")
				m.SymbolKind = SymbolKind.Constructor;
			m.Name = methodReference.Name;
			m.ReturnType = typeReferenceCecilLoader.ReadTypeReference(methodReference.ReturnType);
			m.IsStatic = !methodReference.HasThis;
			for (int i = 0; i < methodReference.GenericParameters.Count; i++) {
				m.TypeParameters.Add(new DefaultUnresolvedTypeParameter(SymbolKind.Method, i, methodReference.GenericParameters[i].Name));
			}
			foreach (var p in methodReference.Parameters) {
				m.Parameters.Add(new DefaultUnresolvedParameter(typeReferenceCecilLoader.ReadTypeReference(p.ParameterType), p.Name));
			}
			var type = declaringTypeReference.Resolve(context);
			return new ResolvedFakeMethod(m, context.WithCurrentTypeDefinition(type.GetDefinition()), type);
		}

		class ResolvedFakeMethod : DefaultResolvedMethod
		{
			readonly IType declaringType;

			public ResolvedFakeMethod(DefaultUnresolvedMethod unresolved, ITypeResolveContext parentContext, IType declaringType)
				: base(unresolved, parentContext)
			{
				this.declaringType = declaringType;
			}

			public override IType DeclaringType
			{
				get { return declaringType; }
			}
		}
		#endregion

		#region Resolve Property
		public IProperty Resolve(PropertyReference propertyReference)
		{
			if (propertyReference == null)
				throw new ArgumentNullException("propertyReference");
			lock (propertyLookupCache) {
				IProperty property;
				if (!propertyLookupCache.TryGetValue(propertyReference, out property)) {
					property = FindNonGenericProperty(propertyReference);
					if (propertyReference.DeclaringType.IsGenericInstance) {
						var git = (GenericInstanceType)propertyReference.DeclaringType;
						var typeArguments = git.GenericArguments.SelectArray(Resolve);
						property = (IProperty)property.Specialize(new TypeParameterSubstitution(typeArguments, null));
					}
					propertyLookupCache.Add(propertyReference, property);
				}
				return property;
			}
		}

		IProperty FindNonGenericProperty(PropertyReference propertyReference)
		{
			ITypeDefinition typeDef = Resolve(propertyReference.DeclaringType).GetDefinition();
			if (typeDef == null)
				return null;
			var parameterTypes = propertyReference.Parameters.SelectArray(p => Resolve(p.ParameterType));
			var returnType = Resolve(propertyReference.PropertyType);
			foreach (IProperty property in typeDef.Properties) {
				if (property.Name == propertyReference.Name
				    && CompareTypes(property.ReturnType, returnType)
				    && CompareSignatures(property.Parameters, parameterTypes))
					return property;
			}
			return null;
		}
		#endregion

		#region Resolve Event
		public IEvent Resolve(EventReference eventReference)
		{
			if (eventReference == null)
				throw new ArgumentNullException("propertyReference");
			lock (eventLookupCache) {
				IEvent ev;
				if (!eventLookupCache.TryGetValue(eventReference, out ev)) {
					ev = FindNonGenericEvent(eventReference);
					if (eventReference.DeclaringType.IsGenericInstance) {
						var git = (GenericInstanceType)eventReference.DeclaringType;
						var typeArguments = git.GenericArguments.SelectArray(Resolve);
						ev = (IEvent)ev.Specialize(new TypeParameterSubstitution(typeArguments, null));
					}
					eventLookupCache.Add(eventReference, ev);
				}
				return ev;
			}
		}

		IEvent FindNonGenericEvent(EventReference eventReference)
		{
			ITypeDefinition typeDef = Resolve(eventReference.DeclaringType).GetDefinition();
			if (typeDef == null)
				return null;
			var returnType = Resolve(eventReference.EventType);
			foreach (IEvent ev in typeDef.Events) {
				if (ev.Name == eventReference.Name && CompareTypes(ev.ReturnType, returnType))
					return ev;
			}
			return null;
		}
		#endregion
	}
}