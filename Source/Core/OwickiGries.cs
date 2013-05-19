﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Boogie;
using System.Diagnostics;
using System.Diagnostics.Contracts;

namespace Microsoft.Boogie
{
    class ProcedureInfo
    {
        // containsYield is true iff there is an implementation that contains a yield statement
        public bool containsYield;
        // isThreadStart is true iff there is an async call to the procedure
        public bool isThreadStart;
        // isEntrypoint is true iff the procedure is an entrypoint
        public bool isEntrypoint;
        // isAtomic is true if there are no yields transitively in any implementation
        public bool isAtomic;
        // inParallelCall is true iff this procedure is involved in some parallel call
        public bool inParallelCall;
        public ProcedureInfo()
        {
            containsYield = false;
            isThreadStart = false;
            isAtomic = true;
            inParallelCall = false;
        }
    }
    /*
    struct YieldInfo
    {
        public YieldCmd yieldCmd;
        public CmdSeq cmds;
        public YieldInfo(YieldCmd yieldCmd, CmdSeq cmds)
        {
            this.yieldCmd = yieldCmd;
            this.cmds = cmds;
        }
    }
    */
    class AsyncAndYieldTraverser : StandardVisitor
    {
        Dictionary<string, ProcedureInfo> procNameToInfo = new Dictionary<string, ProcedureInfo>();
        Implementation currentImpl = null;
        public static Dictionary<string, ProcedureInfo> Traverse(Program program)
        {
            AsyncAndYieldTraverser traverser = new AsyncAndYieldTraverser();
            traverser.VisitProgram(program);
            return traverser.procNameToInfo;
        }
        public override Procedure VisitProcedure(Procedure node)
        {
            if (!procNameToInfo.ContainsKey(node.Name))
            {
                procNameToInfo[node.Name] = new ProcedureInfo();
            }
            if (QKeyValue.FindBoolAttribute(node.Attributes, "entrypoint"))
            {
                procNameToInfo[node.Name].isEntrypoint = true;
            }
            if (QKeyValue.FindBoolAttribute(node.Attributes, "yields"))
            {
                procNameToInfo[node.Name].isAtomic = false;
            }
            return base.VisitProcedure(node);
        }
        public override Implementation VisitImplementation(Implementation node)
        {
            currentImpl = node;
            if (!procNameToInfo.ContainsKey(node.Name))
            {
                procNameToInfo[node.Name] = new ProcedureInfo();
            }
            if (QKeyValue.FindBoolAttribute(node.Attributes, "entrypoint"))
            {
                procNameToInfo[node.Name].isEntrypoint = true;
            }
            return base.VisitImplementation(node);
        }
        public override YieldCmd VisitYieldCmd(YieldCmd node)
        {
            procNameToInfo[currentImpl.Name].containsYield = true;
            procNameToInfo[currentImpl.Name].isAtomic = false;
            return base.VisitYieldCmd(node);
        }
        public override Cmd VisitCallCmd(CallCmd node)
        {
            if (!procNameToInfo.ContainsKey(node.Proc.Name))
            {
                procNameToInfo[node.Proc.Name] = new ProcedureInfo();
            }
            if (node.IsAsync)
            {
                procNameToInfo[node.Proc.Name].isThreadStart = true;
            }
            if (node.InParallelWith != null)
            {
                CallCmd iter = node;
                while (iter != null)
                {
                    if (!procNameToInfo.ContainsKey(iter.Proc.Name))
                    {
                        procNameToInfo[iter.Proc.Name] = new ProcedureInfo();
                    }
                    procNameToInfo[iter.Proc.Name].isThreadStart = true;
                    procNameToInfo[iter.Proc.Name].inParallelCall = true;
                    iter = iter.InParallelWith;
                }
            }
            return base.VisitCallCmd(node);
        }
    }

    class AtomicTraverser : StandardVisitor
    {
        Implementation currentImpl;
        static Dictionary<string, ProcedureInfo> procNameToInfo;
        static bool moreProcessingRequired;
        public static void Traverse(Program program, Dictionary<string, ProcedureInfo> procNameToInfo)
        {
            AtomicTraverser.procNameToInfo = procNameToInfo;
            do
            {
                moreProcessingRequired = false;
                AtomicTraverser traverser = new AtomicTraverser();
                traverser.VisitProgram(program);
            } while (moreProcessingRequired);
        }

        public override Implementation VisitImplementation(Implementation node)
        {
            if (!procNameToInfo[node.Name].isAtomic)
                return node;
            currentImpl = node;
            return base.VisitImplementation(node);
        }

        public override Cmd VisitCallCmd(CallCmd node)
        {
            ProcedureInfo info = procNameToInfo[node.callee];
            if (!node.IsAsync && !info.isAtomic)
            {
                procNameToInfo[currentImpl.Name].isAtomic = false;
                moreProcessingRequired = true;
            }
            return base.VisitCallCmd(node);
        }
    }

    public class OwickiGriesTransform
    {
        Dictionary<string, ProcedureInfo> procNameToInfo;
        IdentifierExprSeq globalMods;
        Hashtable ogOldGlobalMap;
        LinearTypechecker linearTypechecker;
        Dictionary<string, Procedure> asyncAndParallelCallDesugarings;
        List<Procedure> yieldCheckerProcs;
        List<Implementation> yieldCheckerImpls;
        Procedure yieldProc;

        public OwickiGriesTransform(LinearTypechecker linearTypechecker)
        {
            this.linearTypechecker = linearTypechecker;
            Program program = linearTypechecker.program;
            procNameToInfo = AsyncAndYieldTraverser.Traverse(program);
            AtomicTraverser.Traverse(program, procNameToInfo);
            ogOldGlobalMap = new Hashtable();
            globalMods = new IdentifierExprSeq();
            foreach (Variable g in program.GlobalVariables())
            {
                var oldg = new GlobalVariable(Token.NoToken, new TypedIdent(Token.NoToken, string.Format("og_global_old_{0}", g.Name), g.TypedIdent.Type));
                ogOldGlobalMap[g] = new IdentifierExpr(Token.NoToken, oldg);
                globalMods.Add(new IdentifierExpr(Token.NoToken, g));
            }
            asyncAndParallelCallDesugarings = new Dictionary<string, Procedure>();
            yieldCheckerProcs = new List<Procedure>();
            yieldCheckerImpls = new List<Implementation>();
            VariableSeq inputs = new VariableSeq();
            foreach (string domainName in linearTypechecker.linearDomains.Keys)
            {
                var domain = linearTypechecker.linearDomains[domainName];
                Formal f = new Formal(Token.NoToken, new TypedIdent(Token.NoToken, domainName + "_in", new MapType(Token.NoToken, new TypeVariableSeq(), new TypeSeq(domain.elementType), Type.Bool)), true);
                inputs.Add(f);
            }
            yieldProc = new Procedure(Token.NoToken, "og_yield", new TypeVariableSeq(), inputs, new VariableSeq(), new RequiresSeq(), new IdentifierExprSeq(), new EnsuresSeq());
            yieldProc.AddAttribute("inline", new LiteralExpr(Token.NoToken, Microsoft.Basetypes.BigNum.FromInt(1)));
        }

        private void AddCallToYieldProc(CmdSeq newCmds, Dictionary<string, Variable> domainNameToLocalVar)
        {
            ExprSeq exprSeq = new ExprSeq();
            foreach (string domainName in linearTypechecker.linearDomains.Keys)
            {
                exprSeq.Add(new IdentifierExpr(Token.NoToken, domainNameToLocalVar[domainName]));
            }
            CallCmd yieldCallCmd = new CallCmd(Token.NoToken, yieldProc.Name, exprSeq, new IdentifierExprSeq());
            yieldCallCmd.Proc = yieldProc;
            newCmds.Add(yieldCallCmd);
        }

        private Dictionary<string, Expr> ComputeAvailableExprs(HashSet<Variable> availableLocalLinearVars, Dictionary<string, Variable> domainNameToInputVar)
        {
            Dictionary<string, Expr> domainNameToExpr = new Dictionary<string, Expr>();
            foreach (var domainName in linearTypechecker.linearDomains.Keys)
            {
                domainNameToExpr[domainName] = new IdentifierExpr(Token.NoToken, domainNameToInputVar[domainName]);
            }
            foreach (Variable v in availableLocalLinearVars)
            {
                var domainName = linearTypechecker.FindDomainName(v);
                var domain = linearTypechecker.linearDomains[domainName];
                IdentifierExpr ie = new IdentifierExpr(Token.NoToken, v);
                domainNameToExpr[domainName] = new NAryExpr(Token.NoToken, new FunctionCall(domain.mapOrBool), new ExprSeq(v.TypedIdent.Type is MapType ? ie : linearTypechecker.Singleton(ie, domainName), domainNameToExpr[domainName]));
            }
            return domainNameToExpr;
        }

        private void AddUpdatesToOldGlobalVars(CmdSeq newCmds, Dictionary<string, Variable> domainNameToLocalVar, Dictionary<string, Expr> domainNameToExpr)
        {
            if (ogOldGlobalMap.Count == 0) return;
            List<AssignLhs> lhss = new List<AssignLhs>();
            List<Expr> rhss = new List<Expr>();
            foreach (Variable g in ogOldGlobalMap.Keys)
            {
                lhss.Add(new SimpleAssignLhs(Token.NoToken, (IdentifierExpr)ogOldGlobalMap[g]));
                rhss.Add(new IdentifierExpr(Token.NoToken, g));
            }
            newCmds.Add(new AssignCmd(Token.NoToken, lhss, rhss));

            lhss = new List<AssignLhs>();
            rhss = new List<Expr>();
            foreach (var domainName in linearTypechecker.linearDomains.Keys)
            {
                lhss.Add(new SimpleAssignLhs(Token.NoToken, new IdentifierExpr(Token.NoToken, domainNameToLocalVar[domainName])));
                rhss.Add(domainNameToExpr[domainName]);
            }
            newCmds.Add(new AssignCmd(Token.NoToken, lhss, rhss));
        }

        private void DesugarYield(YieldCmd yieldCmd, CmdSeq cmds, CmdSeq newCmds, Dictionary<string, Variable> domainNameToInputVar, Dictionary<string, Variable> domainNameToLocalVar)
        {
            AddCallToYieldProc(newCmds, domainNameToLocalVar);

            if (globalMods.Length > 0)
            {
                newCmds.Add(new HavocCmd(Token.NoToken, globalMods));
            }
            Dictionary<string, Expr> domainNameToExpr = ComputeAvailableExprs(linearTypechecker.availableLocalLinearVars[yieldCmd], domainNameToInputVar);
            AddUpdatesToOldGlobalVars(newCmds, domainNameToLocalVar, domainNameToExpr);

            for (int j = 0; j < cmds.Length; j++)
            {
                PredicateCmd predCmd = (PredicateCmd)cmds[j];
                newCmds.Add(new AssumeCmd(Token.NoToken, predCmd.Expr));
            }
        }

        public Procedure DesugarParallelCallCmd(CallCmd callCmd, out List<Expr> ins, out List<IdentifierExpr> outs)
        {
            List<string> parallelCalleeNames = new List<string>();
            CallCmd iter = callCmd;
            ins = new List<Expr>();
            outs = new List<IdentifierExpr>();
            while (iter != null)
            {
                parallelCalleeNames.Add(iter.Proc.Name);
                ins.AddRange(iter.Ins);
                outs.AddRange(iter.Outs);
                iter = iter.InParallelWith;
            }
            parallelCalleeNames.Sort();
            string procName = "og";
            foreach (string calleeName in parallelCalleeNames)
            {
                procName = procName + "_" + calleeName;
            }
            if (asyncAndParallelCallDesugarings.ContainsKey(procName))
                return asyncAndParallelCallDesugarings[procName];

            VariableSeq inParams = new VariableSeq();
            VariableSeq outParams = new VariableSeq();
            RequiresSeq requiresSeq = new RequiresSeq();
            EnsuresSeq ensuresSeq = new EnsuresSeq();
            IdentifierExprSeq ieSeq = new IdentifierExprSeq();
            int count = 0;
            while (callCmd != null)
            {
                Hashtable map = new Hashtable();
                foreach (Variable x in callCmd.Proc.InParams)
                {
                    Variable y = new Formal(Token.NoToken, new TypedIdent(Token.NoToken, "og_" + count + x.Name, x.TypedIdent.Type), true);
                    inParams.Add(y);
                    map[x] = new IdentifierExpr(Token.NoToken, y);
                }
                foreach (Variable x in callCmd.Proc.OutParams)
                {
                    Variable y = new Formal(Token.NoToken, new TypedIdent(Token.NoToken, "og_" + count + x.Name, x.TypedIdent.Type), false);
                    outParams.Add(y);
                    map[x] = new IdentifierExpr(Token.NoToken, y);
                }
                Contract.Assume(callCmd.Proc.TypeParameters.Length == 0);
                Substitution subst = Substituter.SubstitutionFromHashtable(map);
                foreach (Requires req in callCmd.Proc.Requires)
                {
                    requiresSeq.Add(new Requires(req.Free, Substituter.Apply(subst, req.Condition)));
                }
                foreach (IdentifierExpr ie in callCmd.Proc.Modifies)
                {
                    ieSeq.Add(ie);
                }
                foreach (Ensures ens in callCmd.Proc.Ensures)
                {
                    ensuresSeq.Add(new Ensures(ens.Free, Substituter.Apply(subst, ens.Condition)));
                } 
                count++;
                callCmd = callCmd.InParallelWith;
            }

            Procedure proc = new Procedure(Token.NoToken, procName, new TypeVariableSeq(), inParams, outParams, requiresSeq, ieSeq, ensuresSeq);
            asyncAndParallelCallDesugarings[procName] = proc;
            return proc;
        }

        private void CreateYieldCheckerImpl(DeclWithFormals decl, List<CmdSeq> yields, VariableSeq inputs, VariableSeq locals, Hashtable map, Hashtable assumeMap, Hashtable ogOldLocalMap)
        {
            if (yields.Count == 0) return;
            Program program = linearTypechecker.program;
            ProcedureInfo info = procNameToInfo[decl.Name];
            Substitution assumeSubst = Substituter.SubstitutionFromHashtable(assumeMap);
            Substitution oldSubst = Substituter.SubstitutionFromHashtable(ogOldLocalMap);
            Substitution subst = Substituter.SubstitutionFromHashtable(map);
            List<Block> yieldCheckerBlocks = new List<Block>();
            StringSeq labels = new StringSeq();
            BlockSeq labelTargets = new BlockSeq();
            Block yieldCheckerBlock = new Block(Token.NoToken, "exit", new CmdSeq(), new ReturnCmd(Token.NoToken));
            labels.Add(yieldCheckerBlock.Label);
            labelTargets.Add(yieldCheckerBlock);
            yieldCheckerBlocks.Add(yieldCheckerBlock);
            int yieldCount = 0;
            foreach (CmdSeq cs in yields)
            {
                var linearDomains = linearTypechecker.linearDomains;
                CmdSeq newCmds = new CmdSeq();
                foreach (Cmd cmd in cs)
                {
                    PredicateCmd predCmd = (PredicateCmd)cmd;
                    newCmds.Add(new AssumeCmd(Token.NoToken, Substituter.ApplyReplacingOldExprs(assumeSubst, oldSubst, predCmd.Expr)));
                }
                foreach (Cmd cmd in cs)
                {
                    PredicateCmd predCmd = (PredicateCmd)cmd;
                    var newExpr = Substituter.ApplyReplacingOldExprs(subst, oldSubst, predCmd.Expr);
                    if (predCmd is AssertCmd)
                        newCmds.Add(new AssertCmd(predCmd.tok, newExpr));
                    else
                        newCmds.Add(new AssumeCmd(Token.NoToken, newExpr));
                }
                newCmds.Add(new AssumeCmd(Token.NoToken, Expr.False));
                yieldCheckerBlock = new Block(Token.NoToken, "L" + yieldCount++, newCmds, new ReturnCmd(Token.NoToken));
                labels.Add(yieldCheckerBlock.Label);
                labelTargets.Add(yieldCheckerBlock);
                yieldCheckerBlocks.Add(yieldCheckerBlock);
            }
            yieldCheckerBlocks.Insert(0, new Block(Token.NoToken, "enter", new CmdSeq(), new GotoCmd(Token.NoToken, labels, labelTargets)));

            // Create the yield checker procedure
            var yieldCheckerName = string.Format("{0}_YieldChecker_{1}", decl is Procedure ? "Proc" : "Impl", decl.Name);
            var yieldCheckerProc = new Procedure(Token.NoToken, yieldCheckerName, decl.TypeParameters, inputs, new VariableSeq(), new RequiresSeq(), new IdentifierExprSeq(), new EnsuresSeq());
            yieldCheckerProc.AddAttribute("inline", new LiteralExpr(Token.NoToken, Microsoft.Basetypes.BigNum.FromInt(1)));
            yieldCheckerProcs.Add(yieldCheckerProc);

            // Create the yield checker implementation
            var yieldCheckerImpl = new Implementation(Token.NoToken, yieldCheckerName, decl.TypeParameters, inputs, new VariableSeq(), locals, yieldCheckerBlocks);
            yieldCheckerImpl.Proc = yieldCheckerProc;
            yieldCheckerImpl.AddAttribute("inline", new LiteralExpr(Token.NoToken, Microsoft.Basetypes.BigNum.FromInt(1)));
            yieldCheckerImpls.Add(yieldCheckerImpl);
        }

        private void TransformImpl(Implementation impl)
        {
            Program program = linearTypechecker.program;
            ProcedureInfo info = procNameToInfo[impl.Name];

            // Add free requires
            if (!info.isAtomic || info.isEntrypoint || info.isThreadStart)
            {
                Expr freeRequiresExpr = Expr.True;
                foreach (Variable g in ogOldGlobalMap.Keys)
                {
                    freeRequiresExpr = Expr.And(freeRequiresExpr, Expr.Eq(new IdentifierExpr(Token.NoToken, g), (IdentifierExpr)ogOldGlobalMap[g]));
                }
                impl.Proc.Requires.Add(new Requires(true, freeRequiresExpr));
            }

            // Create substitution maps
            Hashtable map = new Hashtable();
            VariableSeq locals = new VariableSeq();
            VariableSeq inputs = new VariableSeq();
            Dictionary<string, Variable> domainNameToInputVar = new Dictionary<string, Variable>();
            Dictionary<string, Variable> domainNameToLocalVar = new Dictionary<string, Variable>();
            int count = 0;
            while (count < impl.Proc.InParams.Length - linearTypechecker.linearDomains.Count)
            {
                Variable inParam = impl.Proc.InParams[count];
                Variable copy = new LocalVariable(Token.NoToken, new TypedIdent(Token.NoToken, inParam.Name, inParam.TypedIdent.Type));
                locals.Add(copy);
                map[impl.InParams[count]] = new IdentifierExpr(Token.NoToken, copy);
                count++;
            }
            foreach (string domainName in linearTypechecker.linearDomains.Keys)
            {
                Variable inParam = impl.Proc.InParams[count];
                Variable copy = new Formal(Token.NoToken, new TypedIdent(Token.NoToken, inParam.Name, inParam.TypedIdent.Type), true);
                domainNameToInputVar[domainName] = copy;
                inputs.Add(copy);
                Variable l = new LocalVariable(Token.NoToken, new TypedIdent(Token.NoToken, inParam.Name + "_local", inParam.TypedIdent.Type));
                domainNameToLocalVar[domainName] = l;
                impl.LocVars.Add(l);
                map[impl.InParams[count]] = new IdentifierExpr(Token.NoToken, copy);
                count++;
            }

            for (int i = 0; i < impl.Proc.OutParams.Length; i++)
            {
                Variable outParam = impl.Proc.OutParams[i];
                var copy = new LocalVariable(Token.NoToken, new TypedIdent(Token.NoToken, outParam.Name, outParam.TypedIdent.Type), outParam.Attributes);
                var ie = new IdentifierExpr(Token.NoToken, copy);
                locals.Add(copy);
                map[impl.OutParams[i]] = ie;
            }
            foreach (Variable local in impl.LocVars)
            {
                var copy = new LocalVariable(Token.NoToken, new TypedIdent(Token.NoToken, local.Name, local.TypedIdent.Type), local.Attributes);
                locals.Add(copy);
                map[local] = new IdentifierExpr(Token.NoToken, copy);
            }
            Hashtable assumeMap = new Hashtable(map);
            foreach (var global in ogOldGlobalMap.Keys)
            {
                assumeMap[global] = ogOldGlobalMap[global];
            }

            Hashtable ogOldLocalMap = new Hashtable();
            foreach (var g in program.GlobalVariables())
            {
                var copy = new LocalVariable(Token.NoToken, new TypedIdent(Token.NoToken, string.Format("og_local_old_{0}", g.Name), g.TypedIdent.Type));
                locals.Add(copy);
                ogOldLocalMap[g] = new IdentifierExpr(Token.NoToken, copy);
            }

            // Collect the yield predicates and desugar yields
            List<CmdSeq> yields = new List<CmdSeq>();
            CmdSeq cmds = new CmdSeq();
            foreach (Block b in impl.Blocks)
            {
                YieldCmd yieldCmd = null;
                CmdSeq newCmds = new CmdSeq();
                for (int i = 0; i < b.Cmds.Length; i++)
                {
                    Cmd cmd = b.Cmds[i];
                    if (cmd is YieldCmd)
                    {
                        yieldCmd = (YieldCmd)cmd;
                        continue;
                    }
                    if (yieldCmd != null)
                    {
                        PredicateCmd pcmd = cmd as PredicateCmd;
                        if (pcmd == null)
                        {
                            DesugarYield(yieldCmd, cmds, newCmds, domainNameToInputVar, domainNameToLocalVar);
                            if (cmds.Length > 0)
                            {
                                yields.Add(cmds);
                                cmds = new CmdSeq();
                            }
                            yieldCmd = null;
                        }
                        else
                        {
                            cmds.Add(pcmd);
                        }
                    }
                    CallCmd callCmd = cmd as CallCmd;
                    if (callCmd != null)
                    {
                        if (callCmd.InParallelWith != null)
                        {
                            List<Expr> ins;
                            List<IdentifierExpr> outs;
                            Procedure dummyParallelCallDesugaring = DesugarParallelCallCmd(callCmd, out ins, out outs);
                            CallCmd dummyCallCmd = new CallCmd(Token.NoToken, dummyParallelCallDesugaring.Name, ins, outs);
                            dummyCallCmd.Proc = dummyParallelCallDesugaring;
                            newCmds.Add(dummyCallCmd);
                        }
                        else if (callCmd.IsAsync)
                        {
                            if (!asyncAndParallelCallDesugarings.ContainsKey(callCmd.Proc.Name))
                            {
                                asyncAndParallelCallDesugarings[callCmd.Proc.Name] = new Procedure(Token.NoToken, string.Format("DummyAsyncTarget_{0}", callCmd.Proc.Name), callCmd.Proc.TypeParameters, callCmd.Proc.InParams, callCmd.Proc.OutParams, callCmd.Proc.Requires, new IdentifierExprSeq(), new EnsuresSeq());
                            }
                            var dummyAsyncTargetProc = asyncAndParallelCallDesugarings[callCmd.Proc.Name];
                            CallCmd dummyCallCmd = new CallCmd(Token.NoToken, dummyAsyncTargetProc.Name, callCmd.Ins, callCmd.Outs);
                            dummyCallCmd.Proc = dummyAsyncTargetProc;
                            newCmds.Add(dummyCallCmd);
                        }
                        else if (procNameToInfo[callCmd.callee].isAtomic)
                        {
                            newCmds.Add(callCmd);
                        }
                        else
                        {
                            AddCallToYieldProc(newCmds, domainNameToLocalVar);
                            newCmds.Add(callCmd);
                            HashSet<Variable> availableLocalLinearVars = new HashSet<Variable>(linearTypechecker.availableLocalLinearVars[callCmd]);
                            foreach (IdentifierExpr ie in callCmd.Outs)
                            {
                                if (linearTypechecker.FindDomainName(ie.Decl) == null) continue;
                                availableLocalLinearVars.Add(ie.Decl);
                            }
                            Dictionary<string, Expr> domainNameToExpr = ComputeAvailableExprs(availableLocalLinearVars, domainNameToInputVar);
                            AddUpdatesToOldGlobalVars(newCmds, domainNameToLocalVar, domainNameToExpr);
                        }
                    }
                    else
                    {
                        newCmds.Add(cmd);
                    }
                }
                if (yieldCmd != null)
                {
                    DesugarYield(yieldCmd, cmds, newCmds, domainNameToInputVar, domainNameToLocalVar);
                    if (cmds.Length > 0)
                    {
                        yields.Add(cmds);
                        cmds = new CmdSeq();
                    }
                }
                if (b.TransferCmd is ReturnCmd && (!info.isAtomic || info.isEntrypoint || info.isThreadStart))
                {
                    AddCallToYieldProc(newCmds, domainNameToLocalVar);
                }
                b.Cmds = newCmds;
            }

            if (!info.isAtomic)
            {
                // Loops
                impl.PruneUnreachableBlocks();
                impl.ComputePredecessorsForBlocks();
                GraphUtil.Graph<Block> g = Program.GraphFromImpl(impl);
                g.ComputeLoops();
                if (g.Reducible)
                {
                    foreach (Block header in g.Headers)
                    {
                        Dictionary<string, Expr> domainNameToExpr = ComputeAvailableExprs(linearTypechecker.availableLocalLinearVars[header], domainNameToInputVar);
                        foreach (Block pred in header.Predecessors)
                        {
                            AddCallToYieldProc(pred.Cmds, domainNameToLocalVar);
                            AddUpdatesToOldGlobalVars(pred.Cmds, domainNameToLocalVar, domainNameToExpr);
                        }
                        CmdSeq newCmds = new CmdSeq();
                        foreach (Variable v in ogOldGlobalMap.Keys)
                        {
                            newCmds.Add(new AssumeCmd(Token.NoToken, Expr.Binary(BinaryOperator.Opcode.Eq, new IdentifierExpr(Token.NoToken, v), (IdentifierExpr)ogOldGlobalMap[v])));
                        }
                        foreach (string domainName in linearTypechecker.linearDomains.Keys)
                        {
                            newCmds.Add(new AssumeCmd(Token.NoToken, Expr.Binary(BinaryOperator.Opcode.Eq, Expr.Ident(domainNameToLocalVar[domainName]), domainNameToExpr[domainName])));
                        }
                        newCmds.AddRange(header.Cmds);
                        header.Cmds = newCmds;
                    }
                }
            }

            {
                // Add initial block
                Dictionary<string, Expr> domainNameToExpr = new Dictionary<string, Expr>();
                foreach (var domainName in linearTypechecker.linearDomains.Keys)
                {
                    domainNameToExpr[domainName] = new IdentifierExpr(Token.NoToken, domainNameToInputVar[domainName]);
                }
                for (int i = 0; i < impl.Proc.InParams.Length - linearTypechecker.linearDomains.Count; i++)
                {
                    Variable v = impl.InParams[i];
                    var domainName = linearTypechecker.FindDomainName(v);
                    if (domainName == null) continue;
                    var domain = linearTypechecker.linearDomains[domainName];
                    IdentifierExpr ie = new IdentifierExpr(Token.NoToken, v);
                    domainNameToExpr[domainName] = new NAryExpr(Token.NoToken, new FunctionCall(domain.mapOrBool), new ExprSeq(v.TypedIdent.Type is MapType ? ie : linearTypechecker.Singleton(ie, domainName), domainNameToExpr[domainName]));
                }
                CmdSeq initCmds = new CmdSeq();
                List<AssignLhs> lhss = new List<AssignLhs>();
                List<Expr> rhss = new List<Expr>();
                foreach (string domainName in linearTypechecker.linearDomains.Keys)
                {
                    lhss.Add(new SimpleAssignLhs(Token.NoToken, new IdentifierExpr(Token.NoToken, domainNameToLocalVar[domainName])));
                    rhss.Add(domainNameToExpr[domainName]);
                }
                initCmds.Add(new AssignCmd(Token.NoToken, lhss, rhss));
                Block initBlock = new Block(Token.NoToken, "linear_init", initCmds, new GotoCmd(Token.NoToken, new StringSeq(impl.Blocks[0].Label), new BlockSeq(impl.Blocks[0])));
                impl.Blocks.Insert(0, initBlock);
            }

            CreateYieldCheckerImpl(impl, yields, inputs, locals, map, assumeMap, ogOldLocalMap);
        }

        public void TransformProc(Procedure proc)
        {
            Program program = linearTypechecker.program;
            ProcedureInfo info = procNameToInfo[proc.Name];
            if (!info.isThreadStart) return;

            // Create substitution maps
            Hashtable map = new Hashtable();
            VariableSeq locals = new VariableSeq();
            VariableSeq inputs = new VariableSeq();
            Dictionary<string, Variable> domainNameToInputVar = new Dictionary<string, Variable>();
            int count = 0;
            while (count < proc.InParams.Length - linearTypechecker.linearDomains.Count)
            {
                Variable inParam = proc.InParams[count];
                Variable copy = new LocalVariable(Token.NoToken, new TypedIdent(Token.NoToken, inParam.Name, inParam.TypedIdent.Type));
                locals.Add(copy);
                map[proc.InParams[count]] = new IdentifierExpr(Token.NoToken, copy);
                count++;
            }
            foreach (string domainName in linearTypechecker.linearDomains.Keys)
            {
                Variable inParam = proc.InParams[count];
                Variable copy = new Formal(Token.NoToken, new TypedIdent(Token.NoToken, inParam.Name, inParam.TypedIdent.Type), true);
                domainNameToInputVar[domainName] = copy;
                inputs.Add(copy);
                map[proc.InParams[count]] = new IdentifierExpr(Token.NoToken, copy);
                count++;
            }
            for (int i = 0; i < proc.OutParams.Length; i++)
            {
                Variable outParam = proc.OutParams[i];
                var copy = new LocalVariable(Token.NoToken, new TypedIdent(Token.NoToken, outParam.Name, outParam.TypedIdent.Type), outParam.Attributes);
                var ie = new IdentifierExpr(Token.NoToken, copy);
                locals.Add(copy);
                map[proc.OutParams[i]] = ie;
            }
            Hashtable assumeMap = new Hashtable(map);
            foreach (var global in ogOldGlobalMap.Keys)
            {
                assumeMap[global] = ogOldGlobalMap[global];
            }

            Hashtable ogOldLocalMap = new Hashtable();
            foreach (var g in program.GlobalVariables())
            {
                var copy = new LocalVariable(Token.NoToken, new TypedIdent(Token.NoToken, string.Format("og_local_old_{0}", g.Name), g.TypedIdent.Type));
                locals.Add(copy);
                ogOldLocalMap[g] = new IdentifierExpr(Token.NoToken, copy);
            }

            // Collect the yield predicates and desugar yields
            List<CmdSeq> yields = new List<CmdSeq>();
            CmdSeq cmds = new CmdSeq();
            if (proc.Requires.Length > 0)
            {
                Dictionary<string, HashSet<Variable>> domainNameToScope = new Dictionary<string, HashSet<Variable>>();
                foreach (var domainName in linearTypechecker.linearDomains.Keys)
                {
                    domainNameToScope[domainName] = new HashSet<Variable>();
                    domainNameToScope[domainName].Add(domainNameToInputVar[domainName]);
                }
                foreach (Variable v in program.GlobalVariables())
                {
                    var domainName = linearTypechecker.FindDomainName(v);
                    if (domainName == null) continue;
                    domainNameToScope[domainName].Add(v);
                }
                for (int i = 0; i < proc.InParams.Length - linearTypechecker.linearDomains.Count; i++)
                {
                    Variable v = proc.InParams[i];
                    var domainName = linearTypechecker.FindDomainName(v);
                    if (domainName == null) continue;
                    domainNameToScope[domainName].Add(v);
                }
                foreach (string domainName in linearTypechecker.linearDomains.Keys)
                {
                    cmds.Add(new AssumeCmd(Token.NoToken, linearTypechecker.DisjointnessExpr(domainName, domainNameToScope[domainName])));
                }
                foreach (Requires r in proc.Requires)
                {
                    if (r.Free)
                    {
                        cmds.Add(new AssumeCmd(r.tok, r.Condition));
                    }
                    else
                    {
                        cmds.Add(new AssertCmd(r.tok, r.Condition));
                    }
                }
                yields.Add(cmds);
                cmds = new CmdSeq();
            }
            if (info.inParallelCall && proc.Ensures.Length > 0)
            {
                Dictionary<string, HashSet<Variable>> domainNameToScope = new Dictionary<string, HashSet<Variable>>();
                foreach (var domainName in linearTypechecker.linearDomains.Keys)
                {
                    domainNameToScope[domainName] = new HashSet<Variable>();
                    domainNameToScope[domainName].Add(domainNameToInputVar[domainName]);
                }
                foreach (Variable v in program.GlobalVariables())
                {
                    var domainName = linearTypechecker.FindDomainName(v);
                    if (domainName == null) continue;
                    domainNameToScope[domainName].Add(v);
                }
                for (int i = 0; i < proc.OutParams.Length; i++)
                {
                    Variable v = proc.OutParams[i];
                    var domainName = linearTypechecker.FindDomainName(v);
                    if (domainName == null) continue;
                    domainNameToScope[domainName].Add(v);
                }
                foreach (string domainName in linearTypechecker.linearDomains.Keys)
                {
                    cmds.Add(new AssumeCmd(Token.NoToken, linearTypechecker.DisjointnessExpr(domainName, domainNameToScope[domainName])));
                }
                foreach (Ensures e in proc.Ensures)
                {
                    if (e.Free)
                    {
                        cmds.Add(new AssumeCmd(e.tok, e.Condition));
                    }
                    else
                    {
                        cmds.Add(new AssertCmd(e.tok, e.Condition));
                    }
                }
                yields.Add(cmds);
                cmds = new CmdSeq();
            }
            CreateYieldCheckerImpl(proc, yields, inputs, locals, map, assumeMap, ogOldLocalMap);
        }

        private void AddYieldProcAndImpl() 
        {
            Program program = linearTypechecker.program;
            VariableSeq inputs = new VariableSeq();
            foreach (string domainName in linearTypechecker.linearDomains.Keys)
            {
                var domain = linearTypechecker.linearDomains[domainName];
                Formal f = new Formal(Token.NoToken, new TypedIdent(Token.NoToken, domainName + "_in", new MapType(Token.NoToken, new TypeVariableSeq(), new TypeSeq(domain.elementType), Type.Bool)), true);
                inputs.Add(f);
            }
            List<Block> blocks = new List<Block>();
            TransferCmd transferCmd = new ReturnCmd(Token.NoToken);
            if (yieldCheckerProcs.Count > 0)
            {
                BlockSeq blockTargets = new BlockSeq();
                StringSeq labelTargets = new StringSeq();
                int labelCount = 0;
                foreach (Procedure proc in yieldCheckerProcs)
                {
                    ExprSeq exprSeq = new ExprSeq();
                    foreach (Variable v in inputs)
                    {
                        exprSeq.Add(new IdentifierExpr(Token.NoToken, v));
                    }
                    CallCmd callCmd = new CallCmd(Token.NoToken, proc.Name, exprSeq, new IdentifierExprSeq());
                    callCmd.Proc = proc;
                    string label = string.Format("L_{0}", labelCount++);
                    Block block = new Block(Token.NoToken, label, new CmdSeq(callCmd), new ReturnCmd(Token.NoToken));
                    labelTargets.Add(label);
                    blockTargets.Add(block);
                    blocks.Add(block);
                }
                transferCmd = new GotoCmd(Token.NoToken, labelTargets, blockTargets);
            }
            blocks.Insert(0, new Block(Token.NoToken, "enter", new CmdSeq(), transferCmd));
            
            var yieldImpl = new Implementation(Token.NoToken, yieldProc.Name, new TypeVariableSeq(), inputs, new VariableSeq(), new VariableSeq(), blocks);
            yieldImpl.Proc = yieldProc;
            yieldImpl.AddAttribute("inline", new LiteralExpr(Token.NoToken, Microsoft.Basetypes.BigNum.FromInt(1)));
            program.TopLevelDeclarations.Add(yieldProc);
            program.TopLevelDeclarations.Add(yieldImpl);
        }

        public void Transform()
        {
            Program program = linearTypechecker.program;
            foreach (var decl in program.TopLevelDeclarations)
            {
                Procedure proc = decl as Procedure;
                if (proc == null) continue;
                TransformProc(proc);
            }

            foreach (var decl in program.TopLevelDeclarations)
            {
                Implementation impl = decl as Implementation;
                if (impl == null) continue;
                TransformImpl(impl);
            }

            foreach (Variable v in ogOldGlobalMap.Keys)
            {
                IdentifierExpr ie = (IdentifierExpr) ogOldGlobalMap[v];
                program.TopLevelDeclarations.Add(ie.Decl);
            }
            foreach (Procedure proc in yieldCheckerProcs)
            {
                program.TopLevelDeclarations.Add(proc);
            }
            foreach (Implementation impl in yieldCheckerImpls)
            {
                program.TopLevelDeclarations.Add(impl);
            }
            foreach (Procedure proc in asyncAndParallelCallDesugarings.Values)
            {
                program.TopLevelDeclarations.Add(proc);
            }
            AddYieldProcAndImpl();
            Microsoft.Boogie.ModSetCollector.DoModSetAnalysis(program);
        }
    }
}
