using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Reflection;
using System.Text;
using UFIDA.U9.Base.PropertyTypes;
using UFIDA.U9.CBO.UIHelper;
using UFIDA.U9.FI.AR.ARMaintenanceUIModel;
using UFSoft.UBF.UI.ControlModel;
using UFSoft.UBF.UI.IView;
using UFSoft.UBF.UI.WebControlAdapter;

namespace UFIDA.U9.SH.LSUIPlugIn
{
    /// <summary>
    /// 应收单
    /// </summary>
    public class ARMainUIFormUIPlugIn : UFSoft.UBF.UI.Custom.ExtendedPartBase
    {
        private ARMainUIFormWebPart curPart = null;

        public override void AfterInit(IPart Part, EventArgs args)
        {
            base.AfterInit(Part, args);

            curPart = Part as ARMainUIFormWebPart;
            if (curPart == null) return;

            //实例化按钮
            IUFButton btn1 = new UFWebButtonAdapter();
            btn1.Text = "折扣分摊";
            btn1.ID = "BtnDistributeDiscounts";
            btn1.AutoPostBack = true;

            //加入Card容器
            IUFCard card = (IUFCard)this.curPart.GetUFControlByName(this.curPart.TopLevelContainer, "Card0");
            card.Controls.Add(btn1);
            Common.CommonFunction.Layout(card, btn1, 14, 0);

            //设置按钮事件
            btn1.Click += new EventHandler(BtnDistributeDiscounts_Click);
        }

        private void BtnDistributeDiscounts_Click(object sender, EventArgs e)
        {
            var headRecord = curPart.Model.ARBillHead.FocusedRecord;
            if (headRecord == null || headRecord.DocStatus > 1) return; // 开立跟核准中才计算
            curPart.DataCollect(); // 数据收集

            if (string.IsNullOrEmpty(headRecord.DescFlexField_PrivateDescSeg17))
            {
                curPart.CurrentSessionState["DiscountCalcMsg"] = $"表头私有段17s整单折扣金额为空，无法折扣分摊！";
                return;
            }

            decimal.TryParse(headRecord.DescFlexField_PrivateDescSeg17, out decimal totalDisMoney);
            if (totalDisMoney != 0)
            {
                DistributeDiscounts(totalDisMoney, headRecord);
            }
            else
            {
                curPart.CurrentSessionState["DiscountCalcMsg"] = $"表头私有段17s整单折扣金额等于0，无法折扣分摊！";
            }
        }

        private void DistributeDiscounts(decimal totalDiscount, ARBillHeadRecord headRecord)
        {
            var records = curPart.Model.ARBillHead_ARBillLines.Records;
            if (records.Count == 0)
            {
                curPart.CurrentSessionState["DiscountCalcMsg"] = $"没有明细可以用来进行折扣分摊！";
                return;
            }

            // 核币金额精度
            var ACMoney = new RoundHelper(headRecord.AC_MoneyRound_Precision, (RoundTypeEnumData)headRecord.AC_MoneyRound_RoundType, headRecord.AC_MoneyRound_RoundValue.GetValueOrDefault());
            // 核币单价精度
            var ACPrice = new RoundHelper(headRecord.AC_PriceRound_Precision, (RoundTypeEnumData)headRecord.AC_PriceRound_RoundType, headRecord.AC_PriceRound_RoundValue.GetValueOrDefault());

            var lineRecords = records.Cast<ARBillHead_ARBillLinesRecord>();
            decimal totalAmount = 0;
            decimal taxPrice;
            decimal beforDisMoney;
            decimal ocMoneyPriceTaxSum;
            foreach (var lineRecord in lineRecords)
            {
                taxPrice = lineRecord.TaxPrice; // 含税单价
                ocMoneyPriceTaxSum = lineRecord.AROCMoneyPriceTaxSum.GetValueOrDefault(); // 价税合计
                decimal.TryParse(lineRecord.DescFlexField_PrivateDescSeg8, out decimal beforDisPrice); // 8s折扣前含税单价
                if (lineRecord.FreeType == 0)
                {
                    // 出货赠品，跳过
                    if (taxPrice == 0) continue;
                    //// 出货不是赠品，应收行是赠品（具体表现为，8s折扣前含税单价来源于出货单行有值，但是应收单行勾选了赠品）
                    //decimal.TryParse(lineRecord.DescFlexField_PrivateDescSeg6, out decimal disMoney0); // 6s折扣金额
                    //totalDiscount -= disMoney0; // 折扣总金额需要减去当前赠品行的折扣金额
                    //continue;
                }
                if (beforDisPrice == 0)
                {
                    curPart.CurrentSessionState["DiscountCalcMsg"] = $"行：{lineRecord.LineNum}，8s折扣前含税单价为空或0，无法进行折扣分摊！";
                    return; // 8s折扣前含税单价为0，不计算
                }
                // 当前单价不等于[8s折扣前含税单价]，根据[8s折扣前含税单价]进行价税合计还原
                if (taxPrice != beforDisPrice)
                {
                    // 根据[8s折扣前含税单价]进行价税合计还原
                    SetBatchPasteData(lineRecord, "TaxPrice", beforDisPrice + "", ACMoney, ACPrice, new List<long>() { lineRecord.ID }, headRecord.IsTaxPrice, headRecord);
                }
                beforDisMoney = lineRecord.AROCMoneyPriceTaxSum.GetValueOrDefault();
                // 价税合计必须大于0.01才能分摊
                if (beforDisMoney > 0.01m)
                {
                    lineRecord.DescFlexField_PrivateDescSeg3 = lineRecord.AROCMoney_TotalMoney.ToString(); // 3s折扣前价税合计
                    lineRecord.DescFlexField_PrivateDescSeg4 = lineRecord.AROCMoney_NonTax.ToString(); // 4s折扣前未税金额
                    lineRecord.DescFlexField_PrivateDescSeg5 = lineRecord.AROCMoney_GoodsTax.ToString(); // 5s折扣前税额
                    totalAmount += beforDisMoney;
                }
                if (taxPrice != beforDisPrice)
                {
                    // 获取到正确的价税合计之后，需要按照原来的价税合计还原回去（按照单价还原可能存在单价未除尽的情况）
                    SetBatchPasteData(lineRecord, "AROCMoneyPriceTaxSum", ocMoneyPriceTaxSum + "", ACMoney, ACPrice, new List<long>() { lineRecord.ID }, headRecord.IsTaxPrice, headRecord);
                }
            }
            if (totalAmount == 0)
            {
                curPart.CurrentSessionState["DiscountCalcMsg"] = $"所有非赠品行的行明细价税合计之和等于0，无法进行折扣分摊！";
                return;
            }
            if (Math.Abs(totalDiscount) >= Math.Abs(totalAmount))
            {
                curPart.CurrentSessionState["DiscountCalcMsg"] = $"整单折扣金额必需小于所有非赠品行的明细行价税合计之和，才能进行折扣分摊！";
                return;
            }

            // 累计折扣金额和
            decimal accumulatedDiscount = 0;
            decimal disMoney = 0;

            // 遍历子实体计算折扣金额
            List<ARBillHead_ARBillLinesRecord> calcLineRecords = new List<ARBillHead_ARBillLinesRecord>();
            decimal rate;
            foreach (var lineRecord in lineRecords)
            {
                if (lineRecord.FreeType == 0) continue; // 赠品，跳过
                // 计算当前子实体的折扣金额
                taxPrice = lineRecord.TaxPrice; // 含税单价
                decimal.TryParse(lineRecord.DescFlexField_PrivateDescSeg3, out beforDisMoney); // 3s折扣前价税合计
                decimal.TryParse(lineRecord.DescFlexField_PrivateDescSeg8, out decimal beforDisPrice); // 8s折扣前含税单价
                if (beforDisMoney <= 0.01m) continue; // 3s折扣前价税合计小于等于0.01，跳过（会导致价税合计=0）
                rate = beforDisMoney / totalAmount;
                disMoney = ACMoney.GetRoundValue(totalDiscount * rate);
                // 为0就给一分钱
                if (disMoney == 0m)
                {
                    if (beforDisMoney > 0) disMoney = 0.01m;
                    else if ((beforDisMoney < 0)) disMoney = -0.01m; // 折扣前价税合计为负数，那就是负一分钱
                }
                // 累计折扣金额
                accumulatedDiscount += disMoney;
                lineRecord.DescFlexField_PrivateDescSeg6 = disMoney + ""; // 6s折扣金额
                lineRecord.DescFlexField_PrivateDescSeg7 = Math.Round(taxPrice / beforDisPrice, 4) + ""; // 7s折扣率
                ocMoneyPriceTaxSum = ACMoney.GetRoundValue(beforDisMoney - disMoney); // 价税合计
                // 价税合计
                SetBatchPasteData(lineRecord, "AROCMoneyPriceTaxSum", ocMoneyPriceTaxSum + "", ACMoney, ACPrice, new List<long>() { lineRecord.ID }, headRecord.IsTaxPrice, headRecord);
                calcLineRecords.Add(lineRecord);
            }

            // 有尾差
            if (totalDiscount - accumulatedDiscount != 0)
            {
                // 尾差分配给最后一个子实体
                var lastLine = calcLineRecords.Last();
                taxPrice = lastLine.TaxPrice; // 含税单价
                decimal.TryParse(lastLine.DescFlexField_PrivateDescSeg8, out decimal beforDisPrice); // 8s折扣前含税单价
                decimal.TryParse(lastLine.DescFlexField_PrivateDescSeg3, out beforDisMoney); // 3s折扣前价税合计
                decimal.TryParse(lastLine.DescFlexField_PrivateDescSeg6, out decimal lstMny); // 6s折扣金额
                disMoney = lstMny + (totalDiscount - accumulatedDiscount);
                lastLine.DescFlexField_PrivateDescSeg6 = disMoney + "";
                lastLine.DescFlexField_PrivateDescSeg7 = Math.Round(taxPrice / beforDisPrice, 4) + "";
                ocMoneyPriceTaxSum = ACMoney.GetRoundValue(beforDisMoney - disMoney);
                // 价税合计
                SetBatchPasteData(lastLine, "AROCMoneyPriceTaxSum", ocMoneyPriceTaxSum + "", ACMoney, ACPrice, new List<long>() { lastLine.ID }, headRecord.IsTaxPrice, headRecord);
            }

            // 重算本币金额
            CalcFCMoneyFromACMoney(headRecord, true);

            // 分摊完直接保存
            curPart.Action.SaveClick(null, null);
            curPart.CurrentSessionState["IsDiscountCalc"] = true; // 标记已经计算过了
            curPart.CurrentSessionState["DiscountCalcMsg"] = $"折扣分摊成功，请核实折扣后金额是否准确！";
        }

        public override void BeforeEventProcess(IPart Part, string eventName, object sender, EventArgs args, out bool executeDefault)
        {
            UFWebButton4ToolbarAdapter adapter = sender as UFWebButton4ToolbarAdapter; //监听事件

            if ((adapter != null) && (adapter.Text == "保存"))
            {
                executeDefault = BeforeSave(Part);
            }

            base.BeforeEventProcess(Part, eventName, sender, args, out executeDefault);
        }

        public override void AfterRender(IPart Part, EventArgs args)
        {
            base.AfterRender(Part, args);
            curPart = Part as ARMainUIFormWebPart;
            if (curPart != null && curPart.CurrentSessionState.ContainsKey("DiscountCalcMsg"))
            {
                string msg = "";
                if (curPart.CurrentSessionState["DiscountCalcMsg"] != null)
                {
                    msg = curPart.CurrentSessionState["DiscountCalcMsg"]?.ToString();
                }
                curPart.CurrentSessionState["DiscountCalcMsg"] = null;
                if (curPart.Model.ErrorMessage.hasErrorMessage)
                {
                    return;
                }
                if (!string.IsNullOrEmpty(msg))
                {
                    var headRecord = curPart.Model.ARBillHead.FocusedRecord;
                    if (headRecord.ID > 0)
                    {
                        msg = $"单号：{headRecord.DocNo}，{msg}";
                    }
                    curPart.ShowWindowStatus(msg, true);
                }
            }
        }

        private bool BeforeSave(IPart Part)
        {
            curPart = Part as ARMainUIFormWebPart;
            if (curPart == null) return true;
            if (curPart != null && curPart.CurrentSessionState.ContainsKey("IsDiscountCalc") && curPart.CurrentSessionState["IsDiscountCalc"] != null)
            {
                if ((bool)curPart.CurrentSessionState["IsDiscountCalc"])
                {
                    curPart.CurrentSessionState["IsDiscountCalc"] = null;
                    return true;
                }
            }
            DiscountCalc();
            return true;
        }

        /// <summary>
        /// 折扣计算
        /// </summary>
        private void DiscountCalc()
        {
            var headRecord = curPart.Model.ARBillHead.FocusedRecord;
            if (headRecord == null || headRecord.DocStatus > 1) return; // 开立跟核准中才计算
            curPart.DataCollect(); // 数据收集

            // 核币金额精度
            var ACMoney = new RoundHelper(headRecord.AC_MoneyRound_Precision, (RoundTypeEnumData)headRecord.AC_MoneyRound_RoundType, headRecord.AC_MoneyRound_RoundValue.GetValueOrDefault());
            // 核币单价精度
            var ACPrice = new RoundHelper(headRecord.AC_PriceRound_Precision, (RoundTypeEnumData)headRecord.AC_PriceRound_RoundType, headRecord.AC_PriceRound_RoundValue.GetValueOrDefault());
            // 进行折扣计算的行号
            List<int> calcLineNums = new List<int>();
            foreach (ARBillHead_ARBillLinesRecord rec in curPart.Model.ARBillHead_ARBillLines.Records)
            {
                if (rec.DataRecordState == DataRowState.Deleted) continue;
                if (DiscountCalc(rec, headRecord, ACMoney, ACPrice))
                {
                    calcLineNums.Add(rec.LineNum);
                }
            }

            if (calcLineNums.Count > 0)
            {
                // 重算本币金额
                CalcFCMoneyFromACMoney(headRecord, true);
                curPart.CurrentSessionState["DiscountCalcMsg"] = $"行：{string.Join(",", calcLineNums)}，进行了折扣计算，请核实折扣后金额是否准确！";
                // 直接保存
                //curPart.Action.SaveClick(null, null);
            }
        }

        private bool DiscountCalc(ARBillHead_ARBillLinesRecord lineRecord, ARBillHeadRecord headRecord, RoundHelper ACMoney, RoundHelper ACPrice)
        {
            decimal.TryParse(lineRecord.DescFlexField_PrivateDescSeg8, out decimal beforDisPrice); // 8s折扣前含税单价
            if (beforDisPrice == 0) return false; // 8s折扣前含税单价为0，不计算

            bool lineIsAdd = lineRecord.ID <= 0; // 是否新增行
            bool hasCalc = false; // 是否需要计算
            bool isChangeDisMoney = false; // 是否改变6s折扣金额
            bool isChangeDisTax = false; // 是否改变7s折扣率

            decimal.TryParse(lineRecord.DescFlexField_PrivateDescSeg3, out decimal beforDisMoney); // 3s折扣前价税合计
            decimal.TryParse(lineRecord.DescFlexField_PrivateDescSeg6, out decimal disMoney); // 6s折扣金额
            decimal.TryParse(lineRecord.DescFlexField_PrivateDescSeg7, out decimal disTax); // 7s折扣率
            decimal taxPrice = lineRecord.TaxPrice; // 含税单价
            decimal amount = lineRecord.PUAmount.GetValueOrDefault(); // 数量
            decimal ocMoneyPriceTaxSum = lineRecord.AROCMoneyPriceTaxSum.GetValueOrDefault(); // 价税合计

            if (lineIsAdd)
            {
                isChangeDisMoney = disMoney != 0;
                isChangeDisTax = disTax > 0;
            }
            else
            {
                isChangeDisMoney = lineRecord.DescFlexField_PrivateDescSeg6 != lineRecord.OriginalValue["DescFlexField_PrivateDescSeg6"]?.ToString();
                isChangeDisTax = lineRecord.DescFlexField_PrivateDescSeg7 != lineRecord.OriginalValue["DescFlexField_PrivateDescSeg7"]?.ToString();
            }

            bool isRestoreByTaxPrice = false;
            decimal beforTotalMoney = ACMoney.GetRoundValue(amount * beforDisPrice); // 根据数量 * 8s折扣前含税单价 计算出来的折扣前价税合计
            // 当前单价不等于[8s折扣前含税单价]，根据[8s折扣前含税单价]进行价税合计还原
            if (beforTotalMoney != beforDisMoney && taxPrice != beforDisPrice)
            {
                SetBatchPasteData(lineRecord, "TaxPrice", beforDisPrice + "", ACMoney, ACPrice, new List<long>() { lineRecord.ID }, headRecord.IsTaxPrice, headRecord);
                isRestoreByTaxPrice = true;
            }

            if (isRestoreByTaxPrice)
            {
                // 还原后重新赋值[3s折扣前价税合计]、[4s折扣前未税金额]、[5s折扣前税额]
                beforDisMoney = lineRecord.AROCMoneyPriceTaxSum.GetValueOrDefault();
                lineRecord.DescFlexField_PrivateDescSeg3 = lineRecord.AROCMoney_TotalMoney.ToString(); // 3s折扣前价税合计
                lineRecord.DescFlexField_PrivateDescSeg4 = lineRecord.AROCMoney_NonTax.ToString(); // 4s折扣前未税金额
                lineRecord.DescFlexField_PrivateDescSeg5 = lineRecord.AROCMoney_GoodsTax.ToString(); // 5s折扣前税额
            }
            else if (string.IsNullOrEmpty(lineRecord.DescFlexField_PrivateDescSeg3) && taxPrice == beforDisPrice)
            {
                beforDisMoney = lineRecord.AROCMoneyPriceTaxSum.GetValueOrDefault();
                lineRecord.DescFlexField_PrivateDescSeg3 = lineRecord.AROCMoney_TotalMoney.ToString(); // 3s折扣前价税合计
                lineRecord.DescFlexField_PrivateDescSeg4 = lineRecord.AROCMoney_NonTax.ToString(); // 4s折扣前未税金额
                lineRecord.DescFlexField_PrivateDescSeg5 = lineRecord.AROCMoney_GoodsTax.ToString(); // 5s折扣前税额
            }

            if (isChangeDisMoney && disMoney != 0)
            {
                ocMoneyPriceTaxSum = ACMoney.GetRoundValue(beforDisMoney - disMoney);
                taxPrice = ocMoneyPriceTaxSum / amount; // 含税单价=(折扣前金额-6s折扣金额)/数量
                disTax = Math.Round(taxPrice / beforDisPrice, 4); // 7s折扣率 = 含税单价/8s折扣前含税单价
                hasCalc = true;
            }
            else if (isChangeDisTax && disTax > 0 && disTax <= 1) // 大于1的不是打折，小于0的白送钱
            {
                disMoney = ACMoney.GetRoundValue((1 - disTax) * beforDisMoney); // 6s折扣金额=(1-7s折扣率)*折扣前金额
                ocMoneyPriceTaxSum = ACMoney.GetRoundValue(beforDisMoney - disMoney);
                taxPrice = ocMoneyPriceTaxSum / amount; // 含税单价=(折扣前金额-6s折扣金额)/数量
                hasCalc = true;
            }
            else
            {
                if (lineIsAdd)
                {
                    // 行新增，如果是单价发生了变化，表示需要进行折扣
                    if (taxPrice != beforDisPrice)
                    {
                        disMoney = beforDisMoney - ocMoneyPriceTaxSum; // 6s折扣金额=折扣前金额-价税合计
                        disTax = Math.Round(taxPrice / beforDisPrice, 4); // 7s折扣率 = 含税单价/8s折扣前含税单价
                        hasCalc = true;
                    }
                }
                else
                {
                    // 假如当前[6s折扣金额]不等于[折扣前价税合计]-[当前价税合计]，且[6s折扣金额][7s折扣率]没有变化
                    // 那就表示是数量、单价联动了价税合计变价，或者直接改变了价税合计
                    if (disMoney != beforDisMoney - ocMoneyPriceTaxSum)
                    {
                        disMoney = beforDisMoney - ocMoneyPriceTaxSum; // 6s折扣金额=折扣前金额-价税合计
                        disTax = Math.Round(taxPrice / beforDisPrice, 4); // 7s折扣率 = 含税单价/8s折扣前含税单价
                        hasCalc = true;
                    }
                }
            }

            if (hasCalc)
            {
                lineRecord.DescFlexField_PrivateDescSeg6 = ACMoney.GetRoundValue(disMoney) + "";
                lineRecord.DescFlexField_PrivateDescSeg7 = disTax + "";
                // 价税合计
                SetBatchPasteData(lineRecord, "AROCMoneyPriceTaxSum", ocMoneyPriceTaxSum + "", ACMoney, ACPrice, new List<long>() { lineRecord.ID }, headRecord.IsTaxPrice, headRecord);
            }
            else if (isRestoreByTaxPrice)
            {
                // 没有进行折扣计算，需要按照原来的价税合计还原回去（按照单价还原可能存在单价未除尽的情况）
                SetBatchPasteData(lineRecord, "AROCMoneyPriceTaxSum", ocMoneyPriceTaxSum + "", ACMoney, ACPrice, new List<long>() { lineRecord.ID }, headRecord.IsTaxPrice, headRecord);
            }

            return hasCalc;
        }

        /// <summary>
        /// 通过调用批量粘贴值的方法，达到值变化的目的
        /// </summary>
        /// <param name="line">粘贴的行</param>
        /// <param name="srcColumnField">PUAmount、TaxPrice、NonTaxPrice、AROCMoney_NonTax、AROCMoneyPriceTaxSum、AROCMoney_GoodsTax</param>
        /// <param name="Pastevalue">值</param>
        /// <param name="roundHelperACMoney">核币金额精度</param>
        /// <param name="roundHelperACPrice">核币单价精度</param>
        /// <param name="modifiedLineIds">要同步修改的行ID集合</param>
        /// <param name="isTaxPrice">是否含税</param>
        /// <param name="head">应收单头</param>
        private void SetBatchPasteData(ARBillHead_ARBillLinesRecord line, string srcColumnField, string Pastevalue, RoundHelper roundHelperACMoney, RoundHelper roundHelperACPrice, List<long> modifiedLineIds, bool isTaxPrice, ARBillHeadRecord head)
        {
            // 获取类型
            Type type = curPart.GetType();
            // 定义参数类型数组
            Type[] parameterTypes = new Type[] { typeof(ARBillHead_ARBillLinesRecord), typeof(string), typeof(string), typeof(RoundHelper), typeof(RoundHelper), typeof(List<long>), typeof(bool), typeof(ARBillHeadRecord), typeof(AR.ARBill.ARItemInfoDTOData) };
            // 获取方法信息
            MethodInfo methodInfo = type.GetMethod("SetBatchPasteData", BindingFlags.NonPublic | BindingFlags.Instance, null, parameterTypes, null);
            if (methodInfo == null) throw new Exception("SetBatchPasteData未找到");
            // 调用方法，传递参数
            methodInfo.Invoke(curPart, new object[] { line, srcColumnField, Pastevalue, roundHelperACMoney, roundHelperACPrice, modifiedLineIds, isTaxPrice, head, null });
        }

        /// <summary>
        /// 根据核币金额计算本币金额
        /// </summary>
        /// <param name="head">应收单头</param>
        /// <param name="allLineCalc">是否重算全部行</param>
        private void CalcFCMoneyFromACMoney(ARBillHeadRecord head, bool allLineCalc)
        {
            // 获取类型
            Type type = curPart.Action.GetType();
            // 定义参数类型数组
            Type[] parameterTypes = new Type[] { typeof(ARBillHeadRecord), typeof(bool) };
            // 获取方法信息
            MethodInfo methodInfo = type.GetMethod("CalcFCMoneyFromACMoney", BindingFlags.NonPublic | BindingFlags.Instance, null, parameterTypes, null);
            if (methodInfo == null) throw new Exception("CalcFCMoneyFromACMoney未找到");
            // 调用方法，传递参数
            methodInfo.Invoke(curPart.Action, new object[] { head, allLineCalc });
        }

    }

}
